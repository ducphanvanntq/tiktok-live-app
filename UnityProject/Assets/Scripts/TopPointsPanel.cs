using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TikTokLiveGame
{
    public sealed class TopPointsPanel : MonoBehaviour
    {
        [Serializable] private sealed class Manifest { public TextureEntry[] textures; }
        [Serializable] private sealed class TextureEntry { public string resource; public int width, height; public Frame[] frames; }
        [Serializable] private sealed class Frame { public string id; public int[] rect; public int[] border; }
        private sealed class Art { public Texture2D Texture; public Rect Pixels; public int[] Border; }
        private sealed class Row
        {
            public string Id, Name, AvatarUrl;
            public Sprite Avatar;
            public int Rank, PreviousRank;
            public float Y, StartY, TargetY, Opacity, StartOpacity, MoveAge, ScoreAge, Pulse;
            public double DisplayPoints, StartPoints;
            public long Points;
            public bool Leaving;
        }
        private readonly Dictionary<string, Art> art = new();
        private readonly List<Row> rows = new();
        private readonly GUIContent text = new();
        private static readonly CultureInfo PointsCulture = CultureInfo.GetCultureInfo("vi-VN");
        private static readonly Color[] RankColors = { new(1f, 0.87f, 0.6f), new(0.78f, 0.89f, 1f), new(1f, 0.72f, 0.49f) };
        private GUIStyle titleStyle, nameStyle, scoreStyle, captionStyle;
        private float opacity, clearSince = -1f;
        private int renderedFrame = -1;
        private Vector2 savedPosition = Vector2.one;
        private Vector2 dragOffset;
        private bool dragging, positionChanged;
        private string positionKey = "TopPoints.Position";
        internal bool IsPositioning { get; private set; }
        internal bool IsDragging => dragging;
        internal bool AssetsReady => art.Count == 10;
        internal float Opacity => opacity;
        internal int ActiveRows => rows.FindAll(r => !r.Leaving).Count;
        internal bool IsAnimating => rows.Exists(r => r.MoveAge < 0.45f || r.ScoreAge < 0.45f);
        internal string[] DisplayedIds => rows.FindAll(r => !r.Leaving).ConvertAll(r => r.Id).ToArray();

        private void Awake()
        {
            // Offline previews have their own layout and never overwrite the operator's position.
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-welcomePreviewPath") >= 0) positionKey += ".Preview";
            LoadPosition();
            TextAsset json = Resources.Load<TextAsset>("TopPoints/manifest");
            if (json == null) { Debug.LogError("TopPoints manifest missing"); return; }
            Manifest manifest = JsonUtility.FromJson<Manifest>(json.text);
            foreach (TextureEntry item in manifest.textures)
            {
                Texture2D texture = Resources.Load<Texture2D>("TopPoints/" + item.resource);
                if (texture == null || texture.width != item.width || texture.height != item.height)
                { Debug.LogError("TopPoints texture is missing or resized: " + item.resource); art.Clear(); return; }
                foreach (Frame frame in item.frames)
                {
                    int[] r = frame.rect;
                    if (r == null || r.Length != 4 || r[2] <= 0 || r[3] <= 0 || r[0] < 0 || r[1] < 0 || r[0]+r[2] > texture.width || r[1]+r[3] > texture.height)
                    { Debug.LogError("Invalid TopPoints atlas rect"); art.Clear(); return; }
                    art.Add(frame.id, new Art { Texture = texture, Pixels = new Rect(r[0], r[1], r[2], r[3]), Border = frame.border });
                }
            }
        }

        internal void SetScores(PointScoreData[] scores, bool reset = false)
        {
            if (reset) { rows.Clear(); opacity = 0f; clearSince = -1f; }
            var wanted = new HashSet<string>();
            for (int i = 0; i < scores.Length && i < 3; i++)
            {
                PointScoreData score = scores[i];
                if (score.points <= 0 || !wanted.Add(score.userId)) continue;
                float target = RowY(i);
                Row row = rows.Find(r => r.Id == score.userId);
                if (row == null)
                {
                    row = new Row { Id = score.userId, Y = target+8f, TargetY = target, Rank = i, PreviousRank = i, MoveAge = 0f };
                    rows.Add(row);
                }
                if (row.Rank != i || row.Leaving || row.Opacity < 1f && row.MoveAge == 0f)
                {
                    row.PreviousRank = row.MoveAge < 0.14f && row.StartOpacity > 0f ? row.PreviousRank : row.Rank;
                    row.StartY = row.Y; row.TargetY = target; row.StartOpacity = row.Opacity; row.MoveAge = 0f; row.Pulse = 1f;
                }
                if (row.Points != score.points)
                { row.StartPoints = row.DisplayPoints; row.Points = score.points; row.ScoreAge = 0f; }
                row.Rank = i;
                row.Leaving = false;
                row.Name = CleanName(score.nickname, score.userId);
                string avatarUrl = score.avatar ?? string.Empty;
                if (row.AvatarUrl != avatarUrl)
                {
                    row.AvatarUrl = avatarUrl; row.Avatar = null;
                    Row captured = row;
                    AvatarService.Instance?.Load(avatarUrl, avatar =>
                    { if (this != null && captured.AvatarUrl == avatarUrl) captured.Avatar = avatar; });
                }
            }
            foreach (Row row in rows)
                if (!wanted.Contains(row.Id) && !row.Leaving)
                {
                    row.PreviousRank = row.MoveAge < 0.14f && row.StartOpacity > 0f ? row.PreviousRank : row.Rank;
                    row.Leaving = true; row.StartY = row.Y; row.StartOpacity = row.Opacity; row.MoveAge = 0f;
                }
            // Interrupted bursts cannot accumulate unlimited outgoing animations.
            while (rows.Count > 6) rows.Remove(rows.Find(r => r.Leaving));
            if (wanted.Count == 0) { rows.Clear(); opacity = 0f; clearSince = -1f; }
        }

        private static string CleanName(string name, string fallback) =>
            string.IsNullOrWhiteSpace(name) ? fallback : name.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        private static float RowY(int rank) => rank == 0 ? 43f : rank == 1 ? 94f : 131f;

        internal Rect GetPixelRect()
        {
            float scale = LayoutScale();
            Vector2 size = new(230f * scale, 174f * scale);
            Rect area = MovementArea();
            return new Rect(new Vector2(
                Mathf.Lerp(area.xMin, Mathf.Max(area.xMin, area.xMax - size.x), savedPosition.x),
                Mathf.Lerp(area.yMin, Mathf.Max(area.yMin, area.yMax - size.y), savedPosition.y)), size);
        }

        private static Rect MovementArea()
        {
            float scale = LayoutScale();
            Rect safe = Screen.safeArea;
            if (safe.width <= 0f || safe.height <= 0f) safe = new Rect(0,0,Screen.width,Screen.height);
            return Rect.MinMaxRect(safe.xMin + 12f * scale, Screen.height - safe.yMax + 12f * scale,
                safe.xMax - 12f * scale, Screen.height - safe.yMin - 28f * scale);
        }
        // Approximately 31% of a portrait viewport's width, with safe-area margins.
        private static float LayoutScale() => Mathf.Max(0.1f, 0.74f*Mathf.Min(Screen.width/540f, Screen.height/960f));

        internal void TogglePositioning()
        {
            FinishDrag();
            IsPositioning = !IsPositioning;
        }

        internal void ResetPosition()
        {
            dragging = false;
            savedPosition = Vector2.one;
            positionChanged = true;
            FinishDrag();
        }

        internal void LoadPosition()
        {
            float x = PlayerPrefs.GetFloat(positionKey + ".X", 1f);
            float y = PlayerPrefs.GetFloat(positionKey + ".Y", 1f);
            savedPosition = new Vector2(float.IsFinite(x) ? Mathf.Clamp01(x) : 1f, float.IsFinite(y) ? Mathf.Clamp01(y) : 1f);
        }

        private void FinishDrag()
        {
            dragging = false;
            if (!positionChanged) return;
            PlayerPrefs.SetFloat(positionKey + ".X", savedPosition.x);
            PlayerPrefs.SetFloat(positionKey + ".Y", savedPosition.y);
            PlayerPrefs.Save();
            positionChanged = false;
        }

        private void OnApplicationFocus(bool focused) { if (!focused) FinishDrag(); }
        private void OnDisable() => FinishDrag();

        internal void HandlePointer(Event input, Vector2 pointer, bool canStart)
        {
            if (input.type == EventType.MouseDown && input.button == 0 && canStart)
            {
                Rect header = GetPixelRect();
                header.height = 37f * LayoutScale();
                if (!header.Contains(pointer)) return;
                dragOffset = pointer - GetPixelRect().position;
                dragging = true;
                input.Use();
            }
            else if (dragging && input.type == EventType.MouseDrag && input.button == 0)
            {
                Rect area = MovementArea();
                Vector2 size = GetPixelRect().size;
                Vector2 target = pointer - dragOffset;
                savedPosition = new Vector2(
                    Mathf.InverseLerp(area.xMin, Mathf.Max(area.xMin, area.xMax - size.x), target.x),
                    Mathf.InverseLerp(area.yMin, Mathf.Max(area.yMin, area.yMax - size.y), target.y));
                positionChanged = true;
                input.Use();
            }
            else if (dragging && input.rawType == EventType.MouseUp && input.button == 0)
            {
                FinishDrag();
                input.Use();
            }
        }

        internal void Draw(PlayerManager players, bool enabledByUser, bool otherUiBlocks)
        {
            if ((!enabledByUser || otherUiBlocks) && !IsPositioning) FinishDrag();
            Matrix4x4 inputMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            int dragControl = GUIUtility.GetControlID(72391, FocusType.Passive);
            HandlePointer(Event.current, Event.current.mousePosition,
                AssetsReady && (IsPositioning || enabledByUser && !otherUiBlocks && opacity > 0.95f));
            if (dragging) GUIUtility.hotControl = dragControl;
            else if (GUIUtility.hotControl == dragControl) GUIUtility.hotControl = 0;
            GUI.matrix = inputMatrix;
            if (Event.current.type != EventType.Repaint) return;
            bool arranging = IsPositioning || dragging;
            Rect pixelRect = GetPixelRect();
            bool blocked = !enabledByUser || otherUiBlocks || !AssetsReady || rows.Count == 0;
            if (!blocked && !arranging) blocked = players.OverlapsScreenRect(Camera.main, Expanded(pixelRect, 12f*LayoutScale()));
            if (IsPositioning && AssetsReady) blocked = false;
            if (blocked) { opacity = 0f; clearSince = -1f; return; }
            if (clearSince < 0f) clearSince = Time.unscaledTime;
            opacity = arranging ? 1f : Mathf.Clamp01((Time.unscaledTime-clearSince-0.5f)/0.2f);
            if (opacity <= 0f) return;
            if (renderedFrame != Time.frameCount)
            {
                renderedFrame = Time.frameCount;
                AnimateRows(Time.unscaledDeltaTime);
            }
            EnsureStyles();
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            float scale = LayoutScale();
            GUI.matrix = Matrix4x4.TRS(new Vector3(pixelRect.x, pixelRect.y), Quaternion.identity, Vector3.one*scale);
            GUI.color = new Color(1f,1f,1f,opacity);
            DrawArt("panel", new Rect(0,0,230,174), true);
            GUI.Label(new Rect(14,11,165,25), "TOP ĐIỂM", titleStyle);
            GUI.Label(new Rect(175,22,39,12), "ĐIỂM", captionStyle);
            if (IsPositioning)
                GUI.Label(new Rect(14,30,204,14), "Kéo tiêu đề · F4 xong · F5 đặt lại", captionStyle);
            GUI.BeginGroup(new Rect(7,37,215,130));
            foreach (Row row in rows)
            {
                int rank = row.MoveAge < 0.14f && row.StartOpacity > 0f ? row.PreviousRank : row.Rank;
                Color c = RankColors[rank];
                float y = row.Y-37f;
                float shift = (1f-row.Opacity)*12f;
                GUI.color = new Color(1f,1f,1f,opacity*row.Opacity);
                if (rank == 0) DrawArt("champion-row", new Rect(1+shift,y-2,213,46),true);
                if (row.Pulse > 0f)
                    WelcomeToast.DrawRoundedTexture(new Rect(1+shift,y-1,211,34), Texture2D.whiteTexture,
                        ScaleMode.StretchToFill, new Color(c.r,c.g,c.b,row.Pulse*0.10f), 7f);
                float av = rank == 0 ? 38f : 30f;
                Rect portrait = new(34+shift,y+1,av,av);
                Rect inner = Expanded(portrait,-3f);
                if (row.Avatar != null)
                    WelcomeToast.DrawRoundedTexture(inner,row.Avatar.texture,ScaleMode.ScaleAndCrop,Color.white,inner.width*0.5f);
                else DrawArt("avatar-fallback",inner);
                DrawArt("avatar-ring-"+(rank+1),portrait);
                DrawArt("rank-"+(rank+1),new Rect(5+shift,y,23,rank == 0 ? 38 : 30));
                string points = ((long)Math.Round(row.DisplayPoints)).ToString("N0",PointsCulture);
                // Keep long totals readable without allowing them into the name column.
                scoreStyle.fontSize = points.Length > 10 ? 8 : points.Length > 7 ? 10 : 12;
                float numberWidth = Mathf.Min(112f,Mathf.Max(46f,scoreStyle.CalcSize(new GUIContent(points)).x+2f));
                float nameWidth = Mathf.Max(12f,130f-numberWidth-5f);
                nameStyle.normal.textColor = c;
                scoreStyle.normal.textColor = c;
                float ty = y + (rank == 0 ? 12f : 8f);
                GUI.Label(new Rect(75+shift,ty,nameWidth,19),Fit(row.Name,nameWidth),nameStyle);
                GUI.Label(new Rect(207-numberWidth+shift,ty,numberWidth,19),points,scoreStyle);
                if (rank < 2) DrawArt("divider",new Rect(75+shift,y+(rank==0?45:34),131,1),true);
            }
            GUI.EndGroup();
            if (IsPositioning && rows.Count == 0)
                GUI.Label(new Rect(14,65,202,44), "Chưa có điểm\nVẫn có thể kéo bảng TOP", nameStyle);
            GUI.color = oldColor;
            GUI.matrix = oldMatrix;
        }

        private void AnimateRows(float dt)
        {
            foreach (Row row in rows)
            {
                row.MoveAge += dt; row.ScoreAge += dt;
                // Clear the old slot before introducing its replacement. Passing
                // full-opacity rows through one another makes names unreadable.
                if (row.Leaving || row.MoveAge < 0.14f)
                {
                    float fade = Mathf.SmoothStep(0f,1f,Mathf.Clamp01(row.MoveAge/0.14f));
                    row.Y = row.StartY;
                    row.Opacity = row.StartOpacity*(1f-fade);
                }
                else
                {
                    float enter = Mathf.SmoothStep(0f,1f,Mathf.Clamp01((row.MoveAge-0.18f)/0.27f));
                    row.Y = row.TargetY+8f*(1f-enter);
                    row.Opacity = enter;
                }
                double s = Mathf.SmoothStep(0f,1f,Mathf.Clamp01(row.ScoreAge/0.4f));
                row.DisplayPoints = row.StartPoints+(row.Points-row.StartPoints)*s;
                row.Pulse = Mathf.Max(0f,row.Pulse-dt*1.8f);
            }
            rows.RemoveAll(r => r.Leaving && r.MoveAge >= 0.14f);
        }

        private string Fit(string value,float width)
        {
            text.text = value;
            if (nameStyle.CalcSize(text).x <= width) return value;
            int[] indices = StringInfo.ParseCombiningCharacters(value);
            for (int i = indices.Length-1; i >= 0; i--)
            { text.text = value.Substring(0,indices[i])+"…"; if(nameStyle.CalcSize(text).x<=width)return text.text; }
            return string.Empty;
        }
        private void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize=18,fontStyle=FontStyle.Bold,richText=false,padding=new RectOffset(),normal={textColor=RankColors[0]} };
            nameStyle = new GUIStyle(titleStyle) {fontSize=12};
            scoreStyle = new GUIStyle(nameStyle) {alignment=TextAnchor.UpperRight};
            captionStyle = new GUIStyle(scoreStyle) {fontSize=8,fontStyle=FontStyle.Normal,normal={textColor=new Color(0.6f,0.7f,0.81f)}};
        }
        private static Rect Expanded(Rect rect,float size) => new(rect.x-size,rect.y-size,rect.width+2*size,rect.height+2*size);

        private void DrawArt(string id,Rect target,bool stretch=false)
        {
            Art a = art[id];
            Rect source = a.Pixels;
            if (stretch && a.Border != null && a.Border.Length==4 && a.Border[0]>0)
            {
                float dl = id=="panel"?12f:10f,dt=id=="panel"?12f:8f;
                float[] sx={source.x,source.x+a.Border[0],source.xMax-a.Border[2],source.xMax};
                float[] sy={source.y,source.y+a.Border[1],source.yMax-a.Border[3],source.yMax};
                float[] dx={target.x,target.x+dl,target.xMax-dl,target.xMax};
                float[] dy={target.y,target.y+dt,target.yMax-dt,target.yMax};
                for(int y=0;y<3;y++)for(int x=0;x<3;x++)
                    Blit(a.Texture,Rect.MinMaxRect(dx[x],dy[y],dx[x+1],dy[y+1]),Rect.MinMaxRect(sx[x],sy[y],sx[x+1],sy[y+1]));
                return;
            }
            if (!stretch)
            {
                float fit=Mathf.Min(target.width/source.width,target.height/source.height);
                Vector2 size=new(source.width*fit,source.height*fit);
                target=new Rect(target.center-size*0.5f,size);
            }
            Blit(a.Texture,target,source);
        }
        private static void Blit(Texture2D texture,Rect target,Rect source) => GUI.DrawTextureWithTexCoords(target,texture,
            new Rect(source.x/texture.width,1f-source.yMax/texture.height,source.width/texture.width,source.height/texture.height),true);
    }
}

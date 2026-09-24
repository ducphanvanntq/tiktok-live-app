using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TikTokLiveGame
{
    // Explicit offline capture, used only with -welcomePreviewPath -topPointsPreview.
    public sealed class TopPointsCaptureHarness : MonoBehaviour
    {
        internal IEnumerator Capture(string directory)
        {
            Directory.CreateDirectory(directory);
            yield return new WaitForSecondsRealtime(3f);
            Require(GetComponent<TikTokWebSocketClient>() == null,"Preview must not connect to live events");
            VerifyModel();
            TikTokGameController game=GetComponent<TikTokGameController>();
            PlayerManager manager=GetComponentInChildren<PlayerManager>();
            TopPointsPanel panel=game.PointsPanel;
            panel.ResetPosition();
            Camera camera=Camera.main;
            ClubCameraController director=camera.GetComponent<ClubCameraController>();
            director.enabled=false;
            camera.transform.position=new Vector3(0f,9.2f,17.2f);
            camera.transform.LookAt(new Vector3(0f,1.25f,-1.2f));
            camera.fieldOfView=45f;
            Require(panel.AssetsReady,"All 10 used atlas sprites must load without resizing");
            Require(panel.ActiveRows==0 && game.PointScores.Length==0,"No points must leave the leaderboard empty");
            yield return Shot(directory,"00-empty.png");

            var people=new[] { Person("a","Minh Anh",30000,1),Person("b","Ngọc Hà",20000,2),Person("c","Gia Huy",10000,3) };
            var roster=people.Select(p=>new TikTokPlayerData{userId=p.userId,nickname=p.nickname}).ToArray();
            Action<TikTokEvent> emit=data=>game.SendMessage("HandleEvent",data);
            emit(new TikTokEvent{type="snapshot",pointsVersion=1,pointsRevision=1,pointScores=people,players=roster});
            yield return new WaitForSecondsRealtime(3f);
            Require(panel.ActiveRows==3,"Exactly three positive-scoring people must render");
            Require(panel.GetPixelRect().width<=Screen.width*0.32f,"The card must stay compact in portrait output");
            Require(panel.Opacity>0.95f,"The bottom-right card must be visible in a clear wide shot");
            Require(manager.Find("a").TopRank==1 && manager.Find("b").TopRank==2,"Stage rank matches point rank");
            yield return Shot(directory,"01-top3.png");

            if (Array.IndexOf(Environment.GetCommandLineArgs(), "-topDragManualPreview") >= 0)
            {
                Rect before = panel.GetPixelRect();
                File.WriteAllText(Path.Combine(directory, "mouse-ready.txt"),
                    $"{before.x}|{before.y}|{before.width}|{before.height}");
                float deadline = Time.realtimeSinceStartup + 90f;
                while (Vector2.Distance(before.position, panel.GetPixelRect().position) < 100f && Time.realtimeSinceStartup < deadline)
                    yield return null;
                while (panel.IsDragging && Time.realtimeSinceStartup < deadline) yield return null;
                Require(Vector2.Distance(before.position, panel.GetPixelRect().position) >= 100f && !panel.IsDragging,
                    "Native window mouse input must move and release the TOP card");
                Require(panel.IsPositioning, "F4 must enable placement using real keyboard input");
                Rect placed = panel.GetPixelRect();
                panel.LoadPosition();
                Require(Vector2.Distance(placed.position, panel.GetPixelRect().position) < 0.1f, "Native mouse release persists position");
                yield return Shot(directory, "native-drag.png");
                File.WriteAllText(Path.Combine(directory, "verification.txt"), "PASS: native F4 and mouse drag, GUI coordinate scaling, release and saved position.\n");
                Debug.Log("TOP_NATIVE_DRAG_OK");
                yield return new WaitForSecondsRealtime(0.5f);
                Application.Quit();
                yield break;
            }

            // Absolute server updates also animate if the user never joined the floor.
            var changed=new[] {Person("b","Ngọc Hà",35000,4),Person("a","Minh Anh",30000,1),Person("d","Bảo Ngọc",25000,5)};
            emit(new TikTokEvent{type="like",userId="d",nickname="Bảo Ngọc",spectatorOnly=true,
                pointsVersion=1,pointsRevision=2,pointScores=changed});
            Require(panel.IsAnimating,"A new leader must animate");
            for(int i=0;i<9;i++)
            {
                yield return new WaitForSecondsRealtime(0.075f);
                yield return Shot(directory,$"change-{i:00}.png");
            }
            Require(game.PointScores[0].userId=="b" && manager.Find("b").TopRank==1,"Leader swap must update stage badges");
            Require(manager.Find("d")==null,"Scoring a spectator must not bypass the join rule");
            Require(!panel.DisplayedIds.Contains("c") && panel.DisplayedIds.Contains("d"),"Outgoing row is removed and new row enters");
            emit(new TikTokEvent{type="like",pointsVersion=1,pointsRevision=2,pointScores=people});
            Require(game.PointScores[0].userId=="b","Repeated/stale revisions must not undo ranking");
            yield return Shot(directory,"02-new-leader.png");

            // Move a real rendered player through the card region: it must vanish
            // the same render frame, including when already fully opaque.
            PlayerActor blocker=manager.Find("c");
            Vector3 savedPosition=blocker.transform.position;
            blocker.enabled=false;
            Rect card=panel.GetPixelRect();
            blocker.transform.position=camera.ScreenToWorldPoint(new Vector3(card.center.x,Screen.height-card.center.y,10f));
            yield return new WaitForEndOfFrame();
            Require(manager.OverlapsScreenRect(camera,card),"Test actor must actually project over the card");
            Require(panel.Opacity==0f,"A visible actor crossing the card must hide it immediately");
            yield return Shot(directory,"03-actor-priority.png");
            blocker.transform.position=savedPosition; blocker.enabled=true;
            yield return new WaitForSecondsRealtime(1.4f);
            Require(panel.Opacity>0.95f,"Card returns after the actor clears its region");

            emit(new TikTokEvent{type="snapshot",pointsVersion=1,pointsRevision=2,pointScores=changed,players=roster});
            Require(game.PointScores[0].points==35000,"Reconnect snapshot must restore totals without adding again");
            // Verify podium ranks do not get renumbered if a higher-ranked spectator is absent.
            emit(new TikTokEvent{type="snapshot",pointsVersion=1,pointsRevision=3,
                pointScores=new[]{Person("spectator","Ngoài sàn",40000,1),Person("a","Minh Anh",30000,2)},players=roster});
            Require(manager.Find("a").TopRank==2,"Missing first-place actor must not promote second place to TOP 1");

            emit(new TikTokEvent{type="snapshot",pointsVersion=1,pointsRevision=4,pointScores=people,players=roster});
            yield return new WaitForSecondsRealtime(2f);
            emit(new TikTokEvent{type="chat",userId="a",nickname="Minh Anh",comment="Xin chào"});
            yield return new WaitForEndOfFrame();
            Require(panel.Opacity>0f,"Chat feed must fit left of the card instead of blanking it");
            yield return Shot(directory,"04-feed-and-top.png");

            // Windows can clamp a 1920px-tall window to monitor height. Capture
            // the portrait viewport at 2x instead of mislabelling a square resize.
            Require(Screen.height>Screen.width,"Preview viewport must remain portrait");
            yield return Shot(directory,"05-portrait-2x.png",2);
            Require(panel.GetPixelRect().xMax<=Screen.width && panel.GetPixelRect().yMax<=Screen.height,"Card fits output viewport");
            yield return new WaitForSecondsRealtime(1f);

            // Partial rankings leave the remaining slots blank, without demo names.
            emit(new TikTokEvent{type="snapshot",pointsVersion=1,pointsRevision=5,
                pointScores=new[]{Person("a","Minh Anh",1,1),Person("b","Không điểm",0,2)},players=roster});
            yield return new WaitForSecondsRealtime(1f);
            Require(panel.ActiveRows==1 && game.PointScores.Length==1,"Zero scores must not fill empty slots");
            yield return Shot(directory,"06-single-score.png");

            // New updates can arrive before a previous transition has finished.
            for(int revision=6;revision<=10;revision++)
            {
                emit(new TikTokEvent{type="like",pointsVersion=1,pointsRevision=revision,
                    pointScores=revision%2==0?changed:people});
                yield return new WaitForSecondsRealtime(0.04f);
            }
            yield return new WaitForSecondsRealtime(1f);
            Require(panel.ActiveRows==3 && !panel.IsAnimating && !panel.DisplayedIds.Contains("c"),
                "Interrupted transitions must settle on the latest three users");
            emit(new TikTokEvent{type="snapshot",pointsVersion=1,pointsRevision=11,
                pointScores=new[]{Person("a","Nguyễn Hoàng Minh Anh rất dài 👑",9007199254740991L,1)},players=roster});
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot(directory,"07-long-name-and-total.png");

            // All director shots at start/mid/end, while the crowd animates.
            MethodInfo configure=typeof(ClubCameraController).GetMethod("ConfigureDirectorShot",BindingFlags.NonPublic|BindingFlags.Static);
            manager.TryGetViewerBounds(out Bounds crowd);
            for(int shot=0;shot<8;shot++) for(int step=0;step<3;step++)
            {
                object[] args={shot,0,step*0.5f,crowd,Vector3.zero,Vector3.zero,45f};
                configure.Invoke(null,args);
                camera.transform.position=(Vector3)args[4];camera.transform.LookAt((Vector3)args[5]);camera.fieldOfView=(float)args[6];
                manager.Find("a").Jump();manager.Find("b").Walk();manager.Find("c").Grow();
                yield return new WaitForSecondsRealtime(0.12f);
                yield return new WaitForEndOfFrame();
                Require(!manager.OverlapsScreenRect(camera,panel.GetPixelRect()) || panel.Opacity==0f,"Visible card must never overlap actors across camera shots");
            }

            emit(new TikTokEvent{type="reset"});
            yield return new WaitForEndOfFrame();
            Require(panel.ActiveRows==0 && panel.Opacity==0f && game.PointScores.Length==0,"Reset leaves no rows or stale animations");
            yield return Shot(directory,"08-reset-empty.png");
            yield return VerifyDragging(panel, camera, directory);
            File.WriteAllText(Path.Combine(directory,"verification.txt"),
                "PASS: empty/partial positive-only TOP3; 10 used assets; point formula; tie order; absolute revisions; reconnect/reset; spectator ranks; enter/exit/reorder animation frames; interrupted transitions; long labels and totals; actor occlusion and recovery; feed coexistence; compact portrait viewport and 2x capture; all eight director shots; empty-card positioning; drag/release; all four edges; saved position reload; resize; lost focus; reset position.\n");
            Debug.Log("TOP_POINTS_PREVIEW_OK");
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        private static IEnumerator VerifyDragging(TopPointsPanel panel, Camera camera, string directory)
        {
            camera.transform.position = new Vector3(0f, 9.2f, 17.2f);
            camera.transform.LookAt(new Vector3(0f, 1.25f, -1.2f));
            camera.fieldOfView = 45f;
            panel.TogglePositioning();
            yield return new WaitForEndOfFrame();
            Require(panel.Opacity == 1f && panel.ActiveRows == 0, "An empty card remains visible for positioning");
            Rect initial = panel.GetPixelRect();
            foreach (Vector2 corner in new[] { new Vector2(-9999, -9999), new Vector2(9999, -9999), new Vector2(-9999, 9999), new Vector2(9999, 9999) })
            {
                Vector2 start = panel.GetPixelRect().position + Vector2.one * 10f;
                panel.HandlePointer(new Event { type = EventType.MouseDown, button = 0 }, start, true);
                panel.HandlePointer(new Event { type = EventType.MouseDrag, button = 0 }, corner, true);
                Require(panel.IsDragging, "Dragging remains active while moving beyond the viewport");
                panel.HandlePointer(new Event { type = EventType.MouseUp, button = 0 }, corner, true);
                Rect moved = panel.GetPixelRect();
                Require(!panel.IsDragging && moved.xMin >= 0 && moved.yMin >= 0 && moved.xMax <= Screen.width && moved.yMax <= Screen.height,
                    "All four drag extremes keep the full card inside the viewport");
            }
            Vector2 grab = panel.GetPixelRect().position + Vector2.one * 10f;
            panel.HandlePointer(new Event { type = EventType.MouseDown, button = 0 }, grab, true);
            Vector2 drop = new(35f, Screen.height * 0.68f);
            panel.HandlePointer(new Event { type = EventType.MouseDrag, button = 0 }, drop, true);
            panel.HandlePointer(new Event { type = EventType.MouseUp, button = 0 }, drop, true);
            Rect saved = panel.GetPixelRect();
            Require(Vector2.Distance(saved.position, initial.position) > 100f, "The card actually moves from the default corner");
            TopPointsPanel reloaded = new GameObject("TOP position reload probe").AddComponent<TopPointsPanel>();
            Require(Vector2.Distance(reloaded.GetPixelRect().position, saved.position) < 0.1f, "A fresh component reloads the saved position");
            Destroy(reloaded.gameObject);
            panel.HandlePointer(new Event { type = EventType.MouseDown, button = 0 }, saved.position + Vector2.one * 10f, false);
            Require(!panel.IsDragging, "A hidden or blocked card cannot capture a click");
            panel.HandlePointer(new Event { type = EventType.MouseDown, button = 0 }, saved.position + Vector2.one * 10f, true);
            panel.SendMessage("OnApplicationFocus", false);
            Require(!panel.IsDragging, "Losing window focus releases a drag");
            yield return Shot(directory, "09-empty-card-positioning.png");
            int width = Screen.width, height = Screen.height;
            Screen.SetResolution(800, 600, false);
            yield return new WaitForSecondsRealtime(1f);
            Rect resized = panel.GetPixelRect();
            Require(resized.xMin >= 0 && resized.yMin >= 0 && resized.xMax <= Screen.width && resized.yMax <= Screen.height,
                "A portrait-to-landscape resize keeps the entire card inside the viewport");
            Screen.SetResolution(width, height, false);
            yield return new WaitForSecondsRealtime(1f);
            Require(Vector2.Distance(saved.position, panel.GetPixelRect().position) < 1f, "Returning to portrait preserves the chosen relative position");
            panel.ResetPosition();
            Require(Vector2.Distance(initial.position, panel.GetPixelRect().position) < 1f, "Reset position restores the original bottom-right layout");
            panel.TogglePositioning();
            yield return new WaitForEndOfFrame();
            Require(panel.Opacity == 0f && !panel.IsPositioning, "Finishing placement restores normal empty-card visibility");
        }

        private static IEnumerator Shot(string directory,string file,int superSize=1)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(directory,file),superSize);
            yield return null;
        }
        private static PointScoreData Person(string id,string name,long points,long order)=>new(){userId=id,nickname=name,points=points,reachedOrder=order};
        private static void VerifyModel()
        {
            var model=new PointsLeaderboard();
            model.Apply(new TikTokEvent{type="like",userId="npc-000",likeCount=999});
            model.Apply(new TikTokEvent{type="chat",userId="empty"});
            Require(model.Top.Length==0,"NPCs and zero scores are excluded");
            model.Apply(new TikTokEvent{type="like",userId="a",likeCount=300});
            model.Apply(new TikTokEvent{type="gift",userId="a",diamondCount=5,repeatCount=5});
            Require(model.Top[0].points==800,"300 likes plus 5 diamonds must be 800 points, no combo multiplication");
            model.Apply(new TikTokEvent{type="like",userId="b",likeCount=800});
            Require(model.Top[0].userId=="a","Earlier equal score wins the tie");
            model.Apply(new TikTokEvent{type="like",userId="b",likeCount=1});
            Require(model.Top[0].userId=="b","Like takeover changes leader");
        }
        private static void Require(bool condition,string message)
        {
            if(condition)return;
            Debug.LogError("TOP_POINTS_PREVIEW_FAILED: "+message);
            Application.Quit(2);
            throw new InvalidOperationException(message);
        }
    }
}

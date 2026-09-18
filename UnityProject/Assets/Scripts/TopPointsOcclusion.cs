using UnityEngine;

namespace TikTokLiveGame
{
    internal static class TopPointsOcclusion
    {
        internal static bool Overlaps(Camera camera, Bounds bounds, Rect panel)
        {
            Vector3 min=bounds.min,max=bounds.max;
            float xMin=float.PositiveInfinity,yMin=float.PositiveInfinity,xMax=float.NegativeInfinity,yMax=float.NegativeInfinity;
            int inFront=0;
            for(int i=0;i<8;i++)
            {
                Vector3 p=camera.WorldToScreenPoint(new Vector3((i&1)==0?min.x:max.x,(i&2)==0?min.y:max.y,(i&4)==0?min.z:max.z));
                if(p.z<camera.nearClipPlane)continue;
                inFront++;
                xMin=Mathf.Min(xMin,p.x);xMax=Mathf.Max(xMax,p.x);
                yMin=Mathf.Min(yMin,Screen.height-p.y);yMax=Mathf.Max(yMax,Screen.height-p.y);
            }
            if(inFront==0)return false;
            // A bounds crossing the near plane is unsafe to approximate using
            // only its visible corners. Hide conservatively for this frame.
            return inFront<8 || panel.Overlaps(Rect.MinMaxRect(xMin,yMin,xMax,yMax));
        }
    }
}

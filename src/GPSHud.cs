// GPS HUD for GTA IV (ScriptHookDotNet script)
// Places the game's route arrow props on the road ahead of the player's vehicle, following the route the
// game's own GPS has plotted (read from game memory, see GameGps.cs).
//
// Written for C# 5 so it builds with the csc.exe that ships with .NET Framework 4.x.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

using GTA;
using GTA.Native;

using Color = System.Drawing.Color;
using Matrix = GTA.Matrix;
using PointF = System.Drawing.PointF;

namespace GPSHud
{
    internal sealed class RouteSnapshot
    {
        public Vector3[] Pts;      // smoothed polyline, road surface height
        public float[] Cum;        // cumulative 2D length at each point
        public float[] ArrowS;     // arrow positions along the route (closer together in curves)
        public float Length;
        public bool EndsAtDestination;
        public bool WrongWay;      // the route continues behind the car
        public int Id;
        public Vector3 Dest;
        public string[] Debug;
    }

    // Where the car is on the route this frame. Built in Tick, read by PerFrameDrawing.
    internal sealed class FrameState
    {
        public RouteSnapshot Route;
        public float S0;
        public float SpeedExtra;   // extra look-ahead (metres) from the car's speed
        public int TickMs;
        public Vector3 CarPos;
    }

    public class GPSHudScript : Script
    {
        // ------------------------------------------------------------------ settings
        bool enabled = true;
        Keys toggleKey = Keys.F7;
        Keys debugKey = Keys.None;
        bool showDebug;
        bool hideWhenHudOff = true;

        float arriveDistance = 20f;

        int refreshMs = 200;
        float rebuildDistance = 5f;
        Vector3 lastBuildPos;
        int glowMaxLights = 3;
        string perfText = "";
        double perfRouteMs, perfFrameMs, perfObjMs;
        readonly System.Diagnostics.Stopwatch perfWatch = new System.Diagnostics.Stopwatch();

        bool followLane = false;
        float maxLaneOffset = 7f;
        float speedLookahead = 2f;      // seconds of driving added to MaxDistance
        float speedMaxDistance = 150f;  // MaxDistance never grows past this

        bool fovHorizontal;

        // 3D object arrows
        const string objModel = "CJ_R_ICON_ARROW";   // the game's flat route arrow
        // The model is a vertical plane with its tip along local +Z: pitching it -90 degrees lays it flat on
        // the road with the tip pointing along the route.
        const float ObjPitch = -90f, ObjYaw = 0f;
        float objHeight = 0.05f;
        float objSink = 0.5f;
        float objVisibleHeight = 1f;
        float objSpacing = 9f;
        float curveTurnDegrees = 15f;   // max heading change between arrows in a bend (0 = off)
        float objMinDistance = 18f;
        float objMaxDistance = 60f;
        int objMaxCount = 10;
        bool fade = true;
        float fadeInDistance = 15f, fadeOutDistance = 6f;
        int fadeTimeMs = 400;
        bool glow = true;
        Color glowColor = Color.FromArgb(255, 255, 110, 0);
        float glowIntensity = 5f, glowRange = 5f, glowHeight = 0.6f;

        // ------------------------------------------------------------------ state
        volatile RouteSnapshot route;   // built in Tick, drawn in PerFrameDrawing
        volatile FrameState frame;
        int lastRouteBuild = -100000;
        int routeIdCounter;
        int lastErrorMs = -100000;

        // per-frame state (Tick only)
        float laneOffset, speedExtra;
        int lastTickMs;

        // object pool (Tick only)
        readonly List<GTA.Object> pool = new List<GTA.Object>();
        readonly List<bool> poolVisible = new List<bool>();
        readonly List<bool> poolOwned = new List<bool>();
        readonly List<Placement> placements = new List<Placement>();
        bool objectsDirty = true;
        bool objModelValid;
        float[,] objRot = new float[3, 3];   // model orientation offset
        Vector3 objLocalOffset;              // keeps the rotated model centred and resting on the road

        GTA.Font debugFont;   // created on the first debug frame

        public GPSHudScript()
        {
            LoadSettings();
            Interval = 0; // every frame, so the 3D arrows stay glued to the road
            Tick += OnTick;
            KeyDown += OnKeyDown;
            PerFrameDrawing += OnDraw;
        }

        // =================================================================================== settings

        void LoadSettings()
        {
            SettingsFile s = Settings;
            if (s == null)
                return;

            enabled = s.GetValueBool("Enabled", "General", enabled);
            toggleKey = Helper.StringToKey(s.GetValueString("ToggleKey", "General", "F7"), toggleKey);
            debugKey = Helper.StringToKey(s.GetValueString("DebugKey", "General", "None"), debugKey);
            showDebug = s.GetValueBool("ShowDebug", "General", showDebug);
            hideWhenHudOff = s.GetValueBool("HideWhenHudOff", "General", hideWhenHudOff);

            arriveDistance = s.GetValueFloat("ArriveDistance", "Destination", arriveDistance);

            refreshMs = Math.Max(50, s.GetValueInteger("RefreshIntervalMs", "Route", refreshMs));
            rebuildDistance = Math.Max(0f, s.GetValueFloat("RebuildDistance", "Route", rebuildDistance));

            followLane = s.GetValueBool("FollowLane", "Arrows", followLane);
            maxLaneOffset = s.GetValueFloat("MaxLaneOffset", "Arrows", maxLaneOffset);
            speedLookahead = Clamp(s.GetValueFloat("SpeedLookahead", "Arrows", speedLookahead), 0f, 10f);
            speedMaxDistance = s.GetValueFloat("SpeedMaxDistance", "Arrows", speedMaxDistance);

            string fovMode = s.GetValueString("FovMode", "Camera", "Vertical");
            fovHorizontal = fovMode != null && fovMode.Trim().Equals("Horizontal", StringComparison.OrdinalIgnoreCase);

            objHeight = s.GetValueFloat("HeightAboveRoad", "Objects", objHeight);
            objSink = Clamp(s.GetValueFloat("Sink", "Objects", objSink), 0f, 0.9f);
            objSpacing = Math.Max(2f, s.GetValueFloat("Spacing", "Objects", objSpacing));
            curveTurnDegrees = Clamp(s.GetValueFloat("CurveTurnDegrees", "Objects", curveTurnDegrees), 0f, 90f);
            objMinDistance = s.GetValueFloat("MinDistance", "Objects", objMinDistance);
            objMaxDistance = s.GetValueFloat("MaxDistance", "Objects", objMaxDistance);
            objMaxCount = Math.Max(1, Math.Min(30, s.GetValueInteger("MaxCount", "Objects", objMaxCount)));
            fade = s.GetValueBool("Fade", "Objects", fade);
            fadeInDistance = Math.Max(0.5f, s.GetValueFloat("FadeInDistance", "Objects", fadeInDistance));
            fadeOutDistance = Math.Max(0.5f, s.GetValueFloat("FadeOutDistance", "Objects", fadeOutDistance));
            fadeTimeMs = Math.Max(1, s.GetValueInteger("FadeTimeMs", "Objects", fadeTimeMs));
            glow = s.GetValueBool("Glow", "Objects", glow);
            glowColor = ParseColor(s.GetValueString("GlowColor", "Objects", null), glowColor);
            glowIntensity = Clamp(s.GetValueFloat("GlowIntensity", "Objects", glowIntensity), 0f, 20f);
            glowRange = Clamp(s.GetValueFloat("GlowRange", "Objects", glowRange), 0.5f, 30f);
            glowHeight = s.GetValueFloat("GlowHeight", "Objects", glowHeight);
            glowMaxLights = Math.Max(0, s.GetValueInteger("GlowMaxLights", "Objects", glowMaxLights));
        }

        void OnKeyDown(object sender, GTA.KeyEventArgs e)
        {
            if (e.Key == toggleKey)
            {
                enabled = !enabled;
                Game.DisplayText(enabled ? "GPS HUD on" : "GPS HUD off", 1500);
            }
            else if (debugKey != Keys.None && e.Key == debugKey)
            {
                showDebug = !showDebug;
            }
        }

        void LogError(Exception ex)
        {
            if (Environment.TickCount - lastErrorMs < 5000)
                return;
            lastErrorMs = Environment.TickCount;
            try { Game.Console.Print("[GPSHud] " + ex); } catch (Exception) { }
        }

        // =================================================================================== tick (route)

        void OnTick(object sender, EventArgs e)
        {
            int now = Environment.TickCount;
            float dt = Clamp((now - lastTickMs) / 1000f, 0f, 0.2f);
            lastTickMs = now;

            FrameState st = null;
            FrameState prev = frame;
            try
            {
                perfWatch.Reset();
                perfWatch.Start();
                if (RouteRebuildDue(now, prev))
                {
                    lastRouteBuild = now;
                    if (prev != null)
                        lastBuildPos = prev.CarPos;
                    route = UpdateRoute();
                    perfRouteMs = Smooth(perfRouteMs, perfWatch.Elapsed.TotalMilliseconds);
                }
                perfWatch.Reset();
                perfWatch.Start();
                st = UpdateFrame(route, dt);
                perfFrameMs = Smooth(perfFrameMs, perfWatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                LogError(ex);
                route = null;
                st = null;
            }
            frame = st;

            try
            {
                perfWatch.Reset();
                perfWatch.Start();
                UpdateObjects(st);
                perfObjMs = Smooth(perfObjMs, perfWatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }

            if (showDebug)
                perfText = string.Format(CultureInfo.InvariantCulture,
                    "perf: route build {0:0.00} ms (when rebuilt)  frame {1:0.00} ms  objects {2:0.00} ms",
                    perfRouteMs, perfFrameMs, perfObjMs);
        }

        static double Smooth(double avg, double sample)
        {
            return avg * 0.9 + sample * 0.1;
        }

        // Rebuilding the route is the expensive part, so only do it when it can actually change:
        // after the car has moved a bit, when the route ahead is getting short, or once a second.
        bool RouteRebuildDue(int now, FrameState prev)
        {
            int since = now - lastRouteBuild;
            if (route == null || route.Pts == null)
                return since >= 100;
            if (since < refreshMs)
                return false;
            if (prev == null || since >= 1000)
                return true;
            if (prev.CarPos.DistanceTo2D(lastBuildPos) >= rebuildDistance)
                return true;
            float needed = MaxAhead(objMaxDistance, prev) + 30f;
            return !route.EndsAtDestination && route.Length - prev.S0 < needed;
        }

        // Works out where the car is on the route this frame.
        FrameState UpdateFrame(RouteSnapshot r, float dt)
        {
            if (r == null || r.Pts == null)
                return null;
            Vehicle veh;
            if (!ShouldShow(out veh))
                return null;

            Vector3 carPos = veh.Position;
            float s0, lateral, offDist;
            ProjectOnRoute(r, carPos, out s0, out lateral, out offDist);
            if (offDist > 15f)
            {
                lastRouteBuild = -100000; // rebuild next tick
                return null;
            }

            float target = followLane ? Clamp(lateral, -maxLaneOffset, maxLaneOffset) : 0f;
            laneOffset += (target - laneOffset) * (1f - (float)Math.Exp(-dt * 4f));

            // the faster the car, the further ahead arrows are shown; smoothed so the far end doesn't
            // pump in and out with every bit of throttle
            float extraTarget = Math.Max(0f, veh.Speed) * speedLookahead;
            speedExtra += (extraTarget - speedExtra) * (1f - (float)Math.Exp(-dt * 1.5f));

            FrameState st = new FrameState();
            st.SpeedExtra = speedExtra;
            st.Route = r;
            st.S0 = s0;
            st.TickMs = Environment.TickCount;
            st.CarPos = carPos;
            return st;
        }

        bool ShouldShow(out Vehicle veh)
        {
            veh = null;
            if (!enabled)
                return false;
            if (Function.Call<bool>("IS_PAUSE_MENU_ACTIVE") || Function.Call<bool>("IS_SCREEN_FADED_OUT"))
                return false;
            if (hideWhenHudOff && !Function.Call<bool>("IS_HUD_PREFERENCE_SWITCHED_ON"))
                return false;

            Player player = Game.LocalPlayer;
            if (player == null || !player.CanControlCharacter)
                return false;
            Ped ped = player.Character;
            if (ped == null || !ped.isAlive || !ped.isSittingInVehicle())
                return false;
            veh = ped.CurrentVehicle;
            return veh != null;
        }

        RouteSnapshot UpdateRoute()
        {
            Vehicle veh;
            if (!ShouldShow(out veh))
                return null;
            if (!GameGps.Available)
                return DebugOnly(GameGps.Status);

            // follow exactly the route the game's GPS has plotted (the minimap line)
            return RouteFromGameGps(veh.Position, veh.Direction);
        }

        RouteSnapshot RouteFromGameGps(Vector3 carPos, Vector3 carFwd)
        {
            Vector3[] g;
            int slot, state;
            if (!GameGps.TryRead(out g, out slot, out state))
                return DebugOnly("game GPS has no route");

            // The game stores the route from where it was planned to the destination, and doesn't trim it
            // as the car drives along, so the car can be anywhere on it: the end nearest the car is not
            // the start (e.g. with the destination under a bridge the car is on). Only reverse when the car
            // is right at the last point and pointing along the route read backwards.
            int last = g.Length - 1;
            float toFirst = LevelDistance(g[0], carPos), toLast = LevelDistance(g[last], carPos);
            if (toLast < 50f && toLast < toFirst * 0.25f)
            {
                Vector3 back = Flat(g[last - 1] - g[last]);
                Vector3 cf0 = Flat(carFwd);
                if (back.Length() > 0.1f && cf0.Length() > 0.1f && Vector3.Dot(back, cf0) > 0f)
                    Array.Reverse(g);
            }
            Vector3 end = g[g.Length - 1];
            if (carPos.DistanceTo2D(end) < arriveDistance && Math.Abs(carPos.Z - end.Z) < 8f)
                return DebugOnly("arrived");

            // GPS points sit only where the road bends (straights can be hundreds of metres long), so
            // split long segments before rounding the corners slightly; otherwise smoothing would cut
            // huge chunks off every corner.
            List<Vector3> dense = new List<Vector3>(g.Length * 4);
            for (int i = 0; i < g.Length - 1; i++)
            {
                Vector3 a = g[i], b = g[i + 1];
                int pieces = Math.Max(1, (int)Math.Ceiling(a.DistanceTo2D(b) / 8f));
                for (int k = 0; k < pieces; k++)
                    dense.Add(Vector3.Lerp(a, b, k / (float)pieces));
            }
            dense.Add(end);
            List<Vector3> pts = Chaikin(dense);

            RouteSnapshot r = new RouteSnapshot();
            r.Pts = pts.ToArray();
            r.Cum = new float[r.Pts.Length];
            for (int i = 1; i < r.Pts.Length; i++)
                r.Cum[i] = r.Cum[i - 1] + r.Pts[i - 1].DistanceTo2D(r.Pts[i]);
            r.Length = r.Cum[r.Cum.Length - 1];
            r.EndsAtDestination = g.Length < GameGps.MaxPoints; // a full buffer means the route was cut short
            r.Id = ++routeIdCounter;
            r.Dest = end;
            r.ArrowS = BuildArrowSchedule(r, objSpacing, curveTurnDegrees);

            // the GPS route starting behind the car means it wants you to turn around
            float s0, lat, off;
            ProjectOnRoute(r, carPos, out s0, out lat, out off);
            Vector3 tan = Flat(PointAt(r, s0 + 4f) - PointAt(r, s0 - 4f));
            Vector3 cf = Flat(carFwd);
            r.WrongWay = tan.Length() > 0.1f && cf.Length() > 0.1f
                         && Vector3.Dot(Vector3.Normalize(tan), Vector3.Normalize(cf)) < -0.5f;

            if (showDebug)
            {
                r.Debug = new string[]
                {
                    string.Format(CultureInfo.InvariantCulture,
                        "GPS HUD  source: game GPS (slot {0}, state {1})  {2} GPS points  route {3:0}m  dest {4:0}m{5}",
                        slot, state, g.Length, r.Length, carPos.DistanceTo2D(end),
                        r.WrongWay ? "  TURN AROUND" : ""),
                    GameGps.Status
                };
            }
            return r;
        }

        RouteSnapshot DebugOnly(string reason)
        {
            if (!showDebug)
                return null;
            RouteSnapshot r = new RouteSnapshot();
            r.Debug = new string[] { "GPS HUD: " + reason };
            return r;
        }

        static List<Vector3> Chaikin(List<Vector3> pts)
        {
            if (pts.Count < 3)
                return new List<Vector3>(pts);
            List<Vector3> o = new List<Vector3>(pts.Count * 2);
            o.Add(pts[0]);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                o.Add(Vector3.Lerp(pts[i], pts[i + 1], 0.25f));
                o.Add(Vector3.Lerp(pts[i], pts[i + 1], 0.75f));
            }
            o.Add(pts[pts.Count - 1]);
            return o;
        }

        // =================================================================================== route queries

        // Map distance, plus any height difference beyond what the route's rough heights can be off by,
        // so a road passing under a bridge doesn't count as the same place as the bridge.
        static float LevelDistance(Vector3 a, Vector3 b)
        {
            return a.DistanceTo2D(b) + Math.Max(0f, Math.Abs(a.Z - b.Z) - 3f);
        }

        // dist is the map (2D) distance to the route; the closest part of the route is chosen with
        // LevelDistance so a car on a bridge stays on the bridge's part of the route.
        static void ProjectOnRoute(RouteSnapshot r, Vector3 p, out float s, out float lateral, out float dist)
        {
            s = 0f; lateral = 0f; dist = float.MaxValue;
            float best = float.MaxValue;
            Vector3 q = Flat(p);
            for (int i = 0; i < r.Pts.Length - 1; i++)
            {
                Vector3 a = Flat(r.Pts[i]), b = Flat(r.Pts[i + 1]);
                Vector3 ab = b - a;
                float len = ab.Length();
                float t = len > 1e-3f ? Clamp(Vector3.Dot(q - a, ab) / (len * len), 0f, 1f) : 0f;
                Vector3 proj = a + ab * t;
                float d = (q - proj).Length();
                float z = r.Pts[i].Z + (r.Pts[i + 1].Z - r.Pts[i].Z) * t;
                float score = d + Math.Max(0f, Math.Abs(p.Z - z) - 3f);
                if (score < best)
                {
                    best = score;
                    dist = d;
                    s = r.Cum[i] + len * t;
                    Vector3 tan = SafeNormalize(ab);
                    lateral = Vector3.Dot(q - proj, new Vector3(tan.Y, -tan.X, 0f));
                }
            }
        }

        static Vector3 PointAt(RouteSnapshot r, float s)
        {
            Vector3[] pts = r.Pts;
            float[] cum = r.Cum;
            if (s <= 0f) return pts[0];
            if (s >= r.Length) return pts[pts.Length - 1];

            int lo = 0, hi = cum.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (cum[mid] <= s) lo = mid; else hi = mid;
            }
            float segLen = cum[hi] - cum[lo];
            float t = segLen > 1e-4f ? (s - cum[lo]) / segLen : 0f;
            return Vector3.Lerp(pts[lo], pts[hi], t);
        }

        // Arrow positions along the route. On a straight they are Spacing apart; through a bend the
        // step shrinks towards ArrowMinSpacing so the chain follows the corner instead of jumping across
        // the apex. Equal arc spacing alone leaves a sharp corner with one arrow before the apex and one
        // after it - a 90-degree turn with a long hole between them, which is what looks too sparse in
        // curves. The step targets at most curveTurnDegrees of heading change per arrow, but never gets
        // closer than ArrowMinSpacing so the arrows cannot overlap.
        static float[] BuildArrowSchedule(RouteSnapshot r, float spacing, float turnDegrees)
        {
            List<float> list = new List<float>(64);
            if (r.Pts == null || r.Pts.Length < 2 || r.Length <= 0.01f)
                return list.ToArray();

            float maxTurn = Clamp(turnDegrees, 0f, 90f) * Deg2Rad;
            float minStep = Math.Min(spacing, ArrowMinSpacing(spacing));
            for (float s = 0f; list.Count < 4096; )
            {
                list.Add(s);
                float probe = Math.Min(spacing, r.Length - s);
                if (probe < 0.5f)
                    break;

                float step = spacing;
                if (maxTurn > 0f)
                {
                    // look both ways so a corner tightens the chain before and after the apex, not just
                    // on the one step that happens to contain it
                    float turn = Math.Max(HeadingChange(r, s, probe), HeadingChange(r, s - probe, probe));
                    if (turn > 1e-3f)
                        step = Math.Min(spacing, Math.Max(minStep, probe * maxTurn / turn));
                }
                s += step;
                if (s > r.Length)
                    break;
            }
            return list.ToArray();
        }

        // Angle between the route's heading at s and at s + probe (2D).
        static float HeadingChange(RouteSnapshot r, float s, float probe)
        {
            Vector3 a = Flat(PointAt(r, s + 1.5f) - PointAt(r, s - 1.5f));
            Vector3 b = Flat(PointAt(r, s + probe + 1.5f) - PointAt(r, s + probe - 1.5f));
            if (a.Length() < 0.1f || b.Length() < 0.1f)
                return 0f;
            float dot = Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f);
            return (float)Math.Acos(dot);
        }

        static float ArrowMinSpacing(float spacing)
        {
            return Math.Max(2.5f, spacing * 0.5f);
        }

        static int FirstSlotAtOrAfter(float[] arrowS, float s)
        {
            int lo = 0, hi = arrowS.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (arrowS[mid] < s) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        static int LastSlotAtOrBefore(float[] arrowS, float s)
        {
            int lo = 0, hi = arrowS.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (arrowS[mid] <= s) lo = mid + 1; else hi = mid;
            }
            return lo - 1;
        }

        // =================================================================================== 3D object arrows

        // MaxDistance stretched by the car's speed, but never past SpeedMaxDistance (nor below MaxDistance).
        float MaxAhead(float baseMax, FrameState st)
        {
            return Math.Max(baseMax, Math.Min(baseMax + st.SpeedExtra, speedMaxDistance));
        }

        void UpdateObjects(FrameState st)
        {
            if (objectsDirty)
            {
                DeletePool();
                objectsDirty = false;
                objModelValid = false;
            }
            if (st == null)
            {
                HideFrom(0);
                return;
            }

            Model model = new Model(objModel);
            if (!Function.Call<bool>("HAS_MODEL_LOADED", model))
            {
                Function.Call("REQUEST_MODEL", model);
                HideFrom(0);
                return;
            }
            if (!objModelValid)
            {
                objModelValid = PrepareModelOrientation();
                if (!objModelValid)
                    return;
            }

            RouteSnapshot r = st.Route;
            float s0 = st.S0;
            if (r.ArrowS == null || r.ArrowS.Length == 0)
            {
                HideFrom(0);
                return;
            }
            float maxDist = MaxAhead(objMaxDistance, st);
            // the extra distance gets extra arrows, so MaxCount keeps the same density at speed.
            // Curves may place up to the ratio between Spacing and the closest curve spacing, so the
            // cap has to grow with it (the hard cap is 30 objects).
            int nominalCount = objMaxCount + (int)Math.Ceiling((maxDist - objMaxDistance) / objSpacing);
            float minArrowSpacing = Math.Min(objSpacing, ArrowMinSpacing(objSpacing));
            int maxCount = Math.Min(30, (int)Math.Ceiling(nominalCount * objSpacing / minArrowSpacing));
            float end = Math.Min(s0 + maxDist, r.EndsAtDestination ? r.Length - 3f : r.Length - 1f);

            // Arrows sit on a fixed schedule of route distances, one arrow every Spacing metres on a
            // straight, and closer together through a bend (see BuildArrowSchedule). Both ends of the
            // visible stretch are found in along-route distance, so the spacing does not depend on the
            // straight-line distance from the car - that used to drop arrows where the road ahead passes
            // close to the car (tight bends, hairpins, roundabouts), leaving holes in the curve.
            int firstSlot = FirstSlotAtOrAfter(r.ArrowS, s0 + objMinDistance);
            int lastSlot = LastSlotAtOrBefore(r.ArrowS, end);
            if (lastSlot < firstSlot)
                lastSlot = firstSlot - 1;
            else
                lastSlot = Math.Min(lastSlot, firstSlot + maxCount - 1);

            // An arrow keeps its slot for as long as the route still passes where that slot put it.
            // The game re-reads the same route every RefreshIntervalMs; when it re-plans the route
            // instead, the route at that distance moves away from the arrow and the slot is filled again.
            for (int i = placements.Count - 1; i >= 0; i--)
            {
                Placement p = placements[i];
                bool keep = p.Slot >= firstSlot && p.Slot <= lastSlot
                            && PointAt(r, p.S).DistanceTo2D(p.Center) < 1.5f;
                if (!keep)
                {
                    ReleasePlacement(p);
                    placements.RemoveAt(i);
                }
            }

            // The route's own height is only a straight line between GPS points that can be hundreds of
            // metres apart, so over a crest or dip it can be metres off the road. New arrows look for the
            // road surface near the height of the previous arrow (or the car) instead, following the
            // route's slope from there. Walking the slots outwards also fills any hole left by an arrow
            // that could not be placed yet, instead of moving the whole chain and leaving a gap for good.
            float refS = s0, refZ = CarGroundZ(st.CarPos);
            for (int slot = firstSlot; slot <= lastSlot; slot++)
            {
                Placement existing = PlacementAtSlot(slot);
                if (existing != null)
                {
                    refS = existing.S;
                    refZ = existing.Road.Z;
                    continue;
                }

                float s = r.ArrowS[slot];
                float expectedZ = refZ + PointAt(r, s).Z - PointAt(r, refS).Z;
                bool grounded;
                Placement p = PlaceOnRoad(r, s, expectedZ, out grounded);
                // no road surface found far ahead: collision there isn't streamed in yet, so try again
                // on a later frame rather than leave an arrow floating or buried for good
                if (p == null || (!grounded && p.Road.DistanceTo2D(st.CarPos) > 40f))
                    break;

                p.Slot = slot;
                p.S = s;
                InsertPlacement(p);
                refS = s;
                refZ = p.Road.Z;
            }

            // give every placement an object; objects are only moved when (re)assigned
            foreach (Placement p in placements)
            {
                if (p.Obj >= 0 && p.Obj < pool.Count && Game.Exists(pool[p.Obj]))
                    continue;

                int idx = FreePoolIndex();
                if (idx < 0)
                {
                    GTA.Object created = World.CreateObject(model, p.Pos);
                    if (created == null || !Game.Exists(created))
                        break;
                    created.FreezePosition = true;
                    created.Collision = false;
                    pool.Add(created);
                    poolVisible.Add(false);
                    poolOwned.Add(false);
                    idx = pool.Count - 1;
                }

                GTA.Object obj = pool[idx];
                obj.Position = p.Pos;
                Function.Call("SET_OBJECT_QUATERNION", obj, p.Qx, p.Qy, p.Qz, p.Qw);
                if (!poolVisible[idx])
                {
                    obj.Visible = true;
                    poolVisible[idx] = true;
                }
                poolOwned[idx] = true;
                p.Obj = idx;
                p.Alpha = -1; // a reused object may still carry another arrow's alpha
            }

            if (fade)
                UpdateFade(s0, maxDist);
            if (glow)
                DrawGlowLights();
        }

        Placement PlacementAtSlot(int slot)
        {
            for (int i = 0; i < placements.Count; i++)
                if (placements[i].Slot == slot)
                    return placements[i];
            return null;
        }

        // Keeps placements ordered by slot (nearest first), which the glow follows.
        void InsertPlacement(Placement p)
        {
            int at = placements.Count;
            while (at > 0 && placements[at - 1].Slot > p.Slot)
                at--;
            placements.Insert(at, p);
        }

        // Fades arrows in at the far end, out as they get close to the car, and in over a short time when
        // they are first placed (route appeared or changed) instead of popping.
        void UpdateFade(float s0, float maxDist)
        {
            int now = Environment.TickCount;
            foreach (Placement p in placements)
            {
                if (p.Obj < 0 || p.Obj >= pool.Count || !Game.Exists(pool[p.Obj]))
                    continue;
                float rel = p.S - s0;
                float a = Clamp((rel - objMinDistance) / fadeOutDistance, 0f, 1f)
                        * Clamp((maxDist - rel) / fadeInDistance, 0f, 1f)
                        * Clamp((now - p.CreatedMs) / (float)fadeTimeMs, 0f, 1f);
                int alpha = (int)Math.Round(a * 255f);
                if (alpha == p.Alpha || (Math.Abs(alpha - p.Alpha) < 4 && alpha != 0 && alpha != 255))
                    continue;
                try
                {
                    Function.Call("SET_OBJECT_ALPHA", pool[p.Obj], alpha);
                    p.Alpha = alpha;
                }
                catch (Exception ex)
                {
                    fade = false;
                    LogError(ex);
                    return;
                }
            }
        }

        sealed class Placement
        {
            public Vector3 Center, Road, Up, Pos;
            public float Qx, Qy, Qz, Qw;
            public int Slot;  // index on the arrow schedule (see BuildArrowSchedule)
            public float S;   // distance along the route
            public int Obj = -1;
            public int Alpha = -1;
            public int CreatedMs = Environment.TickCount;
        }

        // Computes where an arrow sits: on the actual ground at the road centre, tilted to the ground's slope.
        // expectedZ is roughly where the road surface should be; grounded is false if none was found near it.
        Placement PlaceOnRoad(RouteSnapshot r, float s, float expectedZ, out bool grounded)
        {
            grounded = false;
            Vector3 road = PointAt(r, s);
            Vector3 center = road;   // route centre line, before the lane offset and ground snapping
            Vector3 fwd = Flat(PointAt(r, s + 1.5f) - PointAt(r, s - 1.5f));
            if (fwd.Length() < 0.1f)
                return null;
            fwd = Vector3.Normalize(fwd);
            Vector3 right = SafeNormalize(Vector3.Cross(fwd, new Vector3(0f, 0f, 1f)));
            if (followLane)
                road += right * laneOffset;
            road.Z = expectedZ;

            Vector3 c, f, b, rt, lt;
            float surfaceZ;
            bool ok = FindRoadSurface(road, out surfaceZ);
            c = new Vector3(road.X, road.Y, surfaceZ);
            grounded = ok;
            if (ok && GroundAt(c + fwd * 1.5f, out f) && GroundAt(c - fwd * 1.5f, out b)
                   && GroundAt(c + right * 1f, out rt) && GroundAt(c - right * 1f, out lt))
            {
                Vector3 f3 = SafeNormalize(f - b);
                Vector3 r3 = SafeNormalize(rt - lt);
                Vector3 n = SafeNormalize(Vector3.Cross(r3, f3));
                if (n.Z > 0.7f)
                {
                    fwd = SafeNormalize(f3 - n * Vector3.Dot(f3, n));
                    right = SafeNormalize(Vector3.Cross(fwd, n));
                }
                else
                {
                    fwd = SafeNormalize(Vector3.Cross(new Vector3(0f, 0f, 1f), right));
                }
            }
            else
            {
                // no collision loaded here yet: fall back to the route's own height and slope
                if (!ok) c = road;
                fwd = SafeNormalize(PointAt(r, s + 1.5f) - PointAt(r, s - 1.5f));
                right = SafeNormalize(Vector3.Cross(fwd, new Vector3(0f, 0f, 1f)));
            }
            Vector3 up = SafeNormalize(Vector3.Cross(right, fwd));

            Placement p = new Placement();
            p.Center = center;
            p.Road = c;
            p.Up = up;
            p.Pos = c + right * objLocalOffset.X + fwd * objLocalOffset.Y + up * objLocalOffset.Z;

            float[,] basis = new float[3, 3]
            {
                { right.X, fwd.X, up.X },
                { right.Y, fwd.Y, up.Y },
                { right.Z, fwd.Z, up.Z }
            };
            MatrixToQuaternion(Mul(basis, objRot), out p.Qx, out p.Qy, out p.Qz, out p.Qw);
            return p;
        }

        static bool GroundAt(Vector3 p, out Vector3 ground)
        {
            float z = World.GetGroundZ(new Vector3(p.X, p.Y, p.Z + 2f), GroundType.NextBelowCurrent);
            if (z == 0f || Math.Abs(z - p.Z) > 3f)
            {
                ground = p;
                return false;
            }
            ground = new Vector3(p.X, p.Y, z);
            return true;
        }

        // A probe only finds the first surface below (or above) where it starts, so a road just above p.Z,
        // or under bridge structure, is missed by a single probe; closely spaced probes both ways find it.
        static readonly float[] ProbesDown = { 0.5f, 1.5f, 3f, 5f, 8f, 15f, 30f };
        static readonly float[] ProbesUp = { 1f, 3f };

        // Finds the ground surface closest to p.Z, so arrows stay on the road the car is on and not on a
        // bridge or overpass above it or on the ground or a lower deck under it.
        static bool FindRoadSurface(Vector3 p, out float z)
        {
            z = p.Z;
            float best = 4f;
            bool found = false;
            for (int i = 0; i < ProbesDown.Length + ProbesUp.Length; i++)
            {
                bool down = i < ProbesDown.Length;
                float g = down
                    ? World.GetGroundZ(new Vector3(p.X, p.Y, p.Z + ProbesDown[i]), GroundType.NextBelowCurrent)
                    : World.GetGroundZ(new Vector3(p.X, p.Y, p.Z - ProbesUp[i - ProbesDown.Length]), GroundType.NextAboveCurrent);
                if (g == 0f)
                    continue; // collision not streamed in
                float d = Math.Abs(g - p.Z);
                if (d < best)
                {
                    best = d;
                    z = g;
                    found = true;
                }
            }
            return found;
        }

        // Height of the road under the car (the car's position is its centre, above the road).
        static float CarGroundZ(Vector3 carPos)
        {
            float g = World.GetGroundZ(new Vector3(carPos.X, carPos.Y, carPos.Z + 1f), GroundType.NextBelowCurrent);
            if (g == 0f || carPos.Z - g > 3f || g > carPos.Z + 1f)
                return carPos.Z - 0.7f;
            return g;
        }

        void DrawGlowLights()
        {
            // placements are sorted nearest first; each dynamic light costs GPU time, so only light a few
            int lights = 0;
            foreach (Placement p in placements)
            {
                if (lights >= glowMaxLights)
                    break;
                if (p.Obj < 0)
                    continue;
                lights++;
                // just above the visible top of the arrow
                Vector3 l = p.Road + p.Up * (objVisibleHeight + objHeight + glowHeight);
                try
                {
                    float dim = fade && p.Alpha >= 0 ? p.Alpha / 255f : 1f;
                    if (dim <= 0.01f)
                        continue;
                    Function.Call("DRAW_LIGHT_WITH_RANGE", l.X, l.Y, l.Z,
                        (int)glowColor.R, (int)glowColor.G, (int)glowColor.B, glowIntensity * dim, glowRange);
                }
                catch (Exception ex)
                {
                    glow = false;
                    LogError(ex);
                    return;
                }
            }
        }

        int FreePoolIndex()
        {
            for (int i = 0; i < pool.Count; i++)
            {
                if (poolOwned[i])
                    continue;
                if (!Game.Exists(pool[i]))
                    continue;
                return i;
            }
            return -1;
        }

        void ReleasePlacement(Placement p)
        {
            if (p.Obj < 0 || p.Obj >= pool.Count)
                return;
            poolOwned[p.Obj] = false;
            if (poolVisible[p.Obj] && Game.Exists(pool[p.Obj]))
                pool[p.Obj].Visible = false;
            poolVisible[p.Obj] = false;
            p.Obj = -1;
        }

        void HideFrom(int index)
        {
            for (int i = index; i < pool.Count; i++)
            {
                poolOwned[i] = false;
                if (!poolVisible[i])
                    continue;
                if (Game.Exists(pool[i]))
                    pool[i].Visible = false;
                poolVisible[i] = false;
            }
            if (index == 0)
                placements.Clear();
        }

        void DeletePool()
        {
            for (int i = 0; i < pool.Count; i++)
            {
                try
                {
                    if (Game.Exists(pool[i]))
                        pool[i].Delete();
                }
                catch (Exception) { }
            }
            pool.Clear();
            poolVisible.Clear();
            poolOwned.Clear();
            placements.Clear();
        }

        // Builds the model's orientation offset and works out how far to shift the rotated model so it is
        // centred on the route and rests on the road surface, using the model's bounding box.
        bool PrepareModelOrientation()
        {
            Model model = new Model(objModel);
            objRot = Mul(RotZ(ObjYaw), RotX(ObjPitch));

            Vector3 min = Vector3.Zero, max = Vector3.Zero;
            model.GetDimensions(ref min, ref max);
            if ((max - min).Length() < 0.01f)
                return false; // not loaded yet
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);
                float z = Apply(objRot, c).Z;
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
            // props can't be scaled, so to make an arrow look lower it is sunk partly into the road
            objVisibleHeight = (maxZ - minZ) * (1f - objSink);
            Vector3 centre = Apply(objRot, (min + max) * 0.5f);
            objLocalOffset = new Vector3(-centre.X, -centre.Y, -minZ - (maxZ - minZ) * objSink + objHeight);
            return true;
        }

        static float[,] RotX(float deg)
        {
            double a = deg * Math.PI / 180.0; float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return new float[3, 3] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
        }

        static float[,] RotY(float deg)
        {
            double a = deg * Math.PI / 180.0; float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return new float[3, 3] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
        }

        static float[,] RotZ(float deg)
        {
            double a = deg * Math.PI / 180.0; float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
            return new float[3, 3] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } };
        }

        static float[,] Mul(float[,] a, float[,] b)
        {
            float[,] o = new float[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    o[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j] + a[i, 2] * b[2, j];
            return o;
        }

        static Vector3 Apply(float[,] m, Vector3 v)
        {
            return new Vector3(m[0, 0] * v.X + m[0, 1] * v.Y + m[0, 2] * v.Z,
                               m[1, 0] * v.X + m[1, 1] * v.Y + m[1, 2] * v.Z,
                               m[2, 0] * v.X + m[2, 1] * v.Y + m[2, 2] * v.Z);
        }

        static void MatrixToQuaternion(float[,] m, out float x, out float y, out float z, out float w)
        {
            float tr = m[0, 0] + m[1, 1] + m[2, 2];
            if (tr > 0f)
            {
                float s = (float)Math.Sqrt(tr + 1f) * 2f;
                w = 0.25f * s;
                x = (m[2, 1] - m[1, 2]) / s;
                y = (m[0, 2] - m[2, 0]) / s;
                z = (m[1, 0] - m[0, 1]) / s;
            }
            else if (m[0, 0] > m[1, 1] && m[0, 0] > m[2, 2])
            {
                float s = (float)Math.Sqrt(1f + m[0, 0] - m[1, 1] - m[2, 2]) * 2f;
                w = (m[2, 1] - m[1, 2]) / s;
                x = 0.25f * s;
                y = (m[0, 1] + m[1, 0]) / s;
                z = (m[0, 2] + m[2, 0]) / s;
            }
            else if (m[1, 1] > m[2, 2])
            {
                float s = (float)Math.Sqrt(1f + m[1, 1] - m[0, 0] - m[2, 2]) * 2f;
                w = (m[0, 2] - m[2, 0]) / s;
                x = (m[0, 1] + m[1, 0]) / s;
                y = 0.25f * s;
                z = (m[1, 2] + m[2, 1]) / s;
            }
            else
            {
                float s = (float)Math.Sqrt(1f + m[2, 2] - m[0, 0] - m[1, 1]) * 2f;
                w = (m[1, 0] - m[0, 1]) / s;
                x = (m[0, 2] + m[2, 0]) / s;
                y = (m[1, 2] + m[2, 1]) / s;
                z = 0.25f * s;
            }
        }

        // =================================================================================== drawing

        sealed class View
        {
            public Vector3 Pos, Fwd, Right, Up;
            public float Focal, W, H;
        }

        void OnDraw(object sender, GraphicsEventArgs e)
        {
            try
            {
                Draw(e.Graphics);
            }
            catch (Exception ex)
            {
                LogError(ex);
            }
        }

        void Draw(GTA.Graphics g)
        {
            RouteSnapshot r = route;
            if (r == null)
                return;

            // only the debug overlay is drawn here; the arrows themselves are world objects
            if (r.Debug == null)
                return;

            g.Scaling = FontScaling.Pixel;
            if (debugFont == null)
                debugFont = new GTA.Font(16f, FontScaling.Pixel);
            for (int i = 0; i < r.Debug.Length; i++)
                g.DrawText(r.Debug[i], 20f, 20f + i * 18f, Color.White, debugFont);
            g.DrawText(perfText, 20f, 20f + r.Debug.Length * 18f, Color.Yellow, debugFont);

            FrameState st = frame;
            if (r.Pts == null || st == null || st.Route != r || Environment.TickCount - st.TickMs > 300)
                return;
            View v = GetView();
            if (v != null)
                DrawRouteLine(g, r, v);
        }

        View GetView()
        {
            Camera cam = Game.CurrentCamera;
            if (cam == null)
                return null;
            float fov = cam.FOV;
            if (fov < 5f || fov > 140f)
                return null;

            System.Drawing.Size res = Game.Resolution;
            View v = new View();
            v.W = res.Width;
            v.H = res.Height;

            v.Pos = cam.Position;
            v.Fwd = SafeNormalize(cam.Direction);
            v.Right = SafeNormalize(Vector3.Cross(v.Fwd, new Vector3(0f, 0f, 1f)));
            if (v.Fwd.Length() < 0.5f || v.Right.Length() < 0.5f)
                return null;
            v.Up = Vector3.Cross(v.Right, v.Fwd);
            float half = (fovHorizontal ? v.W : v.H) * 0.5f;
            v.Focal = half / (float)Math.Tan(fov * 0.5f * Deg2Rad);
            return v;
        }

        static bool Project(View v, Vector3 p, out PointF result)
        {
            Vector3 d = p - v.Pos;
            float depth = Vector3.Dot(d, v.Fwd);
            if (depth < 0.5f)
            {
                result = PointF.Empty;
                return false;
            }
            float x = Vector3.Dot(d, v.Right), y = Vector3.Dot(d, v.Up);
            result = new PointF(v.W * 0.5f + x / depth * v.Focal, v.H * 0.5f - y / depth * v.Focal);
            return result.X > -v.W && result.X < v.W * 2f && result.Y > -v.H && result.Y < v.H * 2f;
        }

        void DrawRouteLine(GTA.Graphics g, RouteSnapshot r, View v)
        {
            PointF prev = PointF.Empty;
            bool havePrev = false;
            for (int i = 0; i < r.Pts.Length; i++)
            {
                PointF q;
                if (!Project(v, r.Pts[i], out q)) { havePrev = false; continue; }
                if (havePrev)
                    g.DrawLine(prev, q, 2f, Color.FromArgb(160, 255, 255, 255));
                prev = q;
                havePrev = true;
            }
        }

        // =================================================================================== math / parsing

        const float Deg2Rad = (float)(Math.PI / 180.0);

        static Vector3 Flat(Vector3 v) { return new Vector3(v.X, v.Y, 0f); }
        static float Clamp(float v, float lo, float hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static Vector3 SafeNormalize(Vector3 v)
        {
            float l = v.Length();
            return l > 1e-5f ? v / l : Vector3.Zero;
        }

        // "A,R,G,B" or "R,G,B"
        static Color ParseColor(string s, Color fallback)
        {
            if (string.IsNullOrEmpty(s))
                return fallback;
            string[] p = s.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            int[] v = new int[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                if (!int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
                    return fallback;
                v[i] = Math.Max(0, Math.Min(255, v[i]));
            }
            if (v.Length == 4) return Color.FromArgb(v[0], v[1], v[2], v[3]);
            if (v.Length == 3) return Color.FromArgb(255, v[0], v[1], v[2]);
            return fallback;
        }
    }
}

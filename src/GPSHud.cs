// GPS HUD for GTA IV (ScriptHookDotNet script)
// Places the game's route arrow props on the road ahead of the player's vehicle, following the road network
// towards the map waypoint (or a mission blip).
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
        public float Length;
        public bool EndsAtDestination;
        public bool WrongWay;      // the route continues behind the car
        public int Id;
        public Vector3 Dest;
        public string DestKind;
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

    internal struct Cand
    {
        public Vector3 Pos, Dir, Axis;
        public float Dist, Angle, AxisAlign;
        public float DirAlign;   // node's traffic direction vs. the step direction (+1 = with traffic)
    }

    public class GPSHudScript : Script
    {
        // ------------------------------------------------------------------ settings
        bool enabled = true;
        Keys toggleKey = Keys.F7;
        Keys debugKey = Keys.None;
        bool showDebug;
        bool hideWhenHudOff = true;

        bool useWaypoint = true;
        bool useMissionBlips = true;
        HashSet<int> missionIcons = new HashSet<int>();
        HashSet<int> missionColours = new HashSet<int>();
        float arriveDistance = 20f;

        int refreshMs = 200;
        float stepLength = 10f;
        float maxRouteLength = 150f;
        int maxTravelCallsPerBuild = 3;
        int travelBudget;
        bool lastWrongWay;

        // Sign of the game's node headings relative to traffic, learned from AI cars (they always drive
        // with traffic). +1 until there is clear evidence the other way.
        float headingSign = 1f;
        int headingEvidence;
        int lastHeadingSample = -100000;
        bool detectWrongWay = true;
        bool useGameGps = true;
        int wrongWayVotes;
        string wrongWayDebug = "";
        float rebuildDistance = 5f;
        Vector3 lastBuildPos;
        int glowMaxLights = 3;
        string perfText = "";
        double perfRouteMs, perfFrameMs, perfObjMs;
        readonly System.Diagnostics.Stopwatch perfWatch = new System.Diagnostics.Stopwatch();
        int maxCandidates = 8;
        bool useTravelDistance = true;

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
        int lastDestCheck = -100000;
        bool hasDest;
        Vector3 dest, destNode;
        string destKind = "-";
        int travelCalls;
        int lastErrorMs = -100000;
        readonly List<string> blipDebug = new List<string>();
        readonly Dictionary<long, float> travelCache = new Dictionary<long, float>();
        readonly List<Cand> cands = new List<Cand>();

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
            missionIcons.Add(0);
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

            useWaypoint = s.GetValueBool("UseWaypoint", "Destination", useWaypoint);
            useMissionBlips = s.GetValueBool("UseMissionBlips", "Destination", useMissionBlips);
            ParseIntList(s.GetValueString("MissionBlipIcons", "Destination", "0"), missionIcons);
            ParseIntList(s.GetValueString("MissionBlipColours", "Destination", ""), missionColours);
            arriveDistance = s.GetValueFloat("ArriveDistance", "Destination", arriveDistance);

            refreshMs = Math.Max(50, s.GetValueInteger("RefreshIntervalMs", "Route", refreshMs));
            stepLength = Clamp(s.GetValueFloat("StepLength", "Route", stepLength), 4f, 30f);
            maxRouteLength = Clamp(s.GetValueFloat("MaxLength", "Route", maxRouteLength), 30f, 600f);
            maxCandidates = Math.Max(3, Math.Min(16, s.GetValueInteger("NodeCandidates", "Route", maxCandidates)));
            useTravelDistance = s.GetValueBool("UseTravelDistance", "Route", useTravelDistance);
            maxTravelCallsPerBuild = Math.Max(1, s.GetValueInteger("PathfinderCallsPerBuild", "Route", maxTravelCallsPerBuild));
            rebuildDistance = Math.Max(0f, s.GetValueFloat("RebuildDistance", "Route", rebuildDistance));
            detectWrongWay = s.GetValueBool("DetectWrongWay", "Route", detectWrongWay);
            string source = s.GetValueString("Source", "Route", "GameGPS");
            useGameGps = source == null || !source.Trim().Equals("Tracer", StringComparison.OrdinalIgnoreCase);

            followLane = s.GetValueBool("FollowLane", "Arrows", followLane);
            maxLaneOffset = s.GetValueFloat("MaxLaneOffset", "Arrows", maxLaneOffset);
            speedLookahead = Clamp(s.GetValueFloat("SpeedLookahead", "Arrows", speedLookahead), 0f, 10f);
            speedMaxDistance = s.GetValueFloat("SpeedMaxDistance", "Arrows", speedMaxDistance);

            string fovMode = s.GetValueString("FovMode", "Camera", "Vertical");
            fovHorizontal = fovMode != null && fovMode.Trim().Equals("Horizontal", StringComparison.OrdinalIgnoreCase);

            objHeight = s.GetValueFloat("HeightAboveRoad", "Objects", objHeight);
            objSink = Clamp(s.GetValueFloat("Sink", "Objects", objSink), 0f, 0.9f);
            objSpacing = Math.Max(2f, s.GetValueFloat("Spacing", "Objects", objSpacing));
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
                    route = UpdateRoute(now);
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
                    "perf: route build {0:0.00} ms (when rebuilt)  frame {1:0.00} ms  objects {2:0.00} ms  native calls {3}  node cache {4}",
                    perfRouteMs, perfFrameMs, perfObjMs, nativeCalls, nodeCache.Count);
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
            if (!useGameGps || !GameGps.Available)
                needed = Math.Min(needed, maxRouteLength * 0.6f); // the tracer never reaches further anyway
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

        RouteSnapshot UpdateRoute(int now)
        {
            Vehicle veh;
            if (!ShouldShow(out veh))
                return null;

            Vector3 carPos = veh.Position;
            Vector3 carFwd = veh.Direction;

            // Preferred: follow exactly the route the game's GPS has plotted (the minimap line).
            if (useGameGps && GameGps.Available)
                return RouteFromGameGps(carPos, carFwd);

            if (now - lastHeadingSample > 2000)
            {
                lastHeadingSample = now;
                SampleTrafficHeadings(carPos);
            }

            if (now - lastDestCheck > 1000)
            {
                lastDestCheck = now;
                Vector3 d; string kind;
                if (FindDestination(carPos, out d, out kind))
                {
                    if (!hasDest || d.DistanceTo(dest) > 5f)
                    {
                        dest = d;
                        destNode = SnapDestination(d);
                        travelCache.Clear();
                        lastWrongWay = false;
                        wrongWayVotes = 0;
                    }
                    hasDest = true;
                    destKind = kind;
                }
                else
                {
                    hasDest = false;
                    destKind = "-";
                }
            }

            if (!hasDest)
                return DebugOnly("no destination");
            if (carPos.DistanceTo2D(destNode) < arriveDistance)
                return DebugOnly("arrived");

            RouteSnapshot r = BuildRoute(carPos, carFwd, destNode);
            if (r == null)
                return DebugOnly("no road found");

            if (showDebug)
            {
                List<string> l = new List<string>();
                l.Add(string.Format(CultureInfo.InvariantCulture,
                    "GPS HUD  dest:{0}  dist:{1:0}m  route:{2:0}m/{3}pts{4}",
                    destKind, carPos.DistanceTo2D(destNode), r.Length, r.Pts.Length,
                    (r.EndsAtDestination ? " (ends at dest)" : "") + (r.WrongWay ? "  WRONG WAY (route continues behind)" : "")));
                l.Add(string.Format(CultureInfo.InvariantCulture,
                    "travel-distance calls:{0} cache:{1} enabled:{2}", travelCalls, travelCache.Count, useTravelDistance));
                l.Add(wrongWayDebug);
                l.Add(string.Format(CultureInfo.InvariantCulture,
                    "node heading vs traffic: evidence {0:+0;-0;0} -> sign {1:+0;-0}", headingEvidence, headingSign));
                l.AddRange(blipDebug);
                r.Debug = l.ToArray();
            }
            return r;
        }

        RouteSnapshot RouteFromGameGps(Vector3 carPos, Vector3 carFwd)
        {
            Vector3[] g;
            int slot, state;
            if (!GameGps.TryRead(out g, out slot, out state))
                return DebugOnly("game GPS has no route");

            // the route runs from the player to the destination; make sure of the order
            if (g[g.Length - 1].DistanceTo2D(carPos) < g[0].DistanceTo2D(carPos))
                Array.Reverse(g);
            Vector3 end = g[g.Length - 1];
            if (carPos.DistanceTo2D(end) < arriveDistance)
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
            r.DestKind = "game GPS";

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
            List<string> l = new List<string>();
            l.Add("GPS HUD: " + reason + "   dest: " + destKind);
            l.AddRange(blipDebug);
            r.Debug = l.ToArray();
            return r;
        }

        // =================================================================================== destination

        static readonly BlipType[] BlipTypes =
        {
            BlipType.Coordinate, BlipType.Vehicle, BlipType.Ped, BlipType.Object, BlipType.Pickup, BlipType.Contact
        };

        bool FindDestination(Vector3 from, out Vector3 result, out string kind)
        {
            result = Vector3.Zero;
            kind = null;
            if (showDebug)
                blipDebug.Clear();

            if (useWaypoint)
            {
                Blip wp = Game.GetWaypoint();
                if (wp != null && Game.Exists(wp))
                {
                    result = wp.Position;
                    kind = "waypoint";
                    return true;
                }
            }

            if (!useMissionBlips && !showDebug)
                return false;

            bool found = false;
            float best = float.MaxValue;
            for (int t = 0; t < BlipTypes.Length; t++)
            {
                Blip[] blips = Blip.GetAllBlipsOfType(BlipTypes[t]);
                if (blips == null)
                    continue;
                for (int i = 0; i < blips.Length; i++)
                {
                    Blip b = blips[i];
                    if (b == null || !Game.Exists(b))
                        continue;

                    int icon = (int)b.Icon;
                    if (useWaypoint && b.Icon == BlipIcon.Misc_Waypoint)
                    {
                        result = b.Position;
                        kind = "waypoint";
                        return true;
                    }

                    Vector3 pos = b.Position;
                    int colour = (int)b.Color;
                    bool shortRange = b.ShowOnlyWhenNear;
                    float d = from.DistanceTo2D(pos);

                    if (showDebug && d < 3000f && blipDebug.Count < 12)
                        blipDebug.Add(string.Format(CultureInfo.InvariantCulture,
                            "  blip type:{0} icon:{1}({2}) colour:{3}({4}) short:{5} dist:{6:0}m",
                            BlipTypes[t], icon, b.Icon, colour, b.Color, shortRange, d));

                    if (useMissionBlips && !shortRange && d > 10f && d < best
                        && missionIcons.Contains(icon)
                        && (missionColours.Count == 0 || missionColours.Contains(colour)))
                    {
                        found = true;
                        best = d;
                        result = pos;
                        kind = "mission blip (" + BlipTypes[t] + ")";
                    }
                }
            }
            return found;
        }

        // Map waypoints have no height and may be off-road; route to the closest road node instead.
        static Vector3 SnapDestination(Vector3 d)
        {
            Vector3 probe = d;
            if (probe.Z == 0f)
                probe.Z = 30f;
            Vector3 node;
            if (ClosestNode(probe, out node) && node.DistanceTo2D(d) < 150f)
                return node;
            return d;
        }

        // =================================================================================== natives

        struct NodeHit
        {
            public bool Ok;
            public Vector3 Pos;
            public float Heading;
        }

        // Road nodes never move, and the route is re-traced over the same road again and again, so node
        // lookups are cached by (probe position rounded to 1 m, n). This removes most native calls.
        readonly Dictionary<long, NodeHit> nodeCache = new Dictionary<long, NodeHit>();
        readonly Dictionary<long, float> groundCache = new Dictionary<long, float>();
        int nativeCalls;

        static long GridKey(Vector3 p, float cell)
        {
            long x = (long)Math.Round(p.X / cell) + 0x80000;
            long y = (long)Math.Round(p.Y / cell) + 0x80000;
            long z = (long)Math.Round(p.Z / 4f) + 0x80;
            return (x & 0xFFFFF) << 28 | (y & 0xFFFFF) << 8 | (z & 0xFF);
        }

        bool NthNode(Vector3 p, int n, out Vector3 node, out float heading)
        {
            long key = GridKey(p, 1f) * 32 + n;
            NodeHit hit;
            if (!nodeCache.TryGetValue(key, out hit))
            {
                Pointer x = typeof(float), y = typeof(float), z = typeof(float), h = typeof(float);
                bool ok = Function.Call<bool>("GET_NTH_CLOSEST_CAR_NODE_WITH_HEADING", p.X, p.Y, p.Z, n, x, y, z, h);
                nativeCalls++;
                hit.Pos = new Vector3((float)x, (float)y, (float)z);
                hit.Heading = (float)h;
                hit.Ok = ok && (hit.Pos.X != 0f || hit.Pos.Y != 0f);
                if (nodeCache.Count > 50000)
                    nodeCache.Clear();
                nodeCache[key] = hit;
            }
            node = hit.Pos;
            heading = hit.Heading;
            return hit.Ok;
        }

        static bool ClosestNode(Vector3 p, out Vector3 node)
        {
            Pointer x = typeof(float), y = typeof(float), z = typeof(float);
            bool ok = Function.Call<bool>("GET_CLOSEST_CAR_NODE", p.X, p.Y, p.Z, x, y, z);
            node = new Vector3((float)x, (float)y, (float)z);
            return ok && (node.X != 0f || node.Y != 0f);
        }

        float GroundZ(Vector3 p)
        {
            long key = GridKey(p, 0.5f);
            float gz;
            if (!groundCache.TryGetValue(key, out gz))
            {
                gz = World.GetGroundZ(new Vector3(p.X, p.Y, p.Z + 1.5f), GroundType.NextBelowCurrent);
                nativeCalls++;
                // collision that isn't streamed in yet reads as 0; don't cache that
                if (gz != 0f)
                {
                    if (groundCache.Count > 20000)
                        groundCache.Clear();
                    groundCache[key] = gz;
                }
            }
            if (gz == 0f || Math.Abs(gz - p.Z) > 3f)
                return p.Z;
            return gz;
        }

        // Returns the road-network distance, -1 if unknown, or NaN if this route build has used up its
        // budget of pathfinder calls (each one is a full A* search inside the game, so they're spread out).
        float TravelDistance(Vector3 from, Vector3 to)
        {
            long key = ((long)(uint)((int)Math.Round(from.X) + 100000) << 20) | (uint)((int)Math.Round(from.Y) + 100000);
            float cached;
            if (travelCache.TryGetValue(key, out cached))
                return cached;
            if (useTravelDistance && travelBudget <= 0)
                return float.NaN;
            travelBudget--;

            float r = -1f;
            if (useTravelDistance)
            {
                try
                {
                    r = Function.Call<float>("CALCULATE_TRAVEL_DISTANCE_BETWEEN_NODES", from.X, from.Y, from.Z, to.X, to.Y, to.Z);
                    travelCalls++;
                }
                catch (Exception ex)
                {
                    useTravelDistance = false;
                    LogError(ex);
                }
            }
            if (float.IsNaN(r) || float.IsInfinity(r) || r <= 0f || r > 100000f)
                r = -1f;

            if (travelCache.Count > 2048)
                travelCache.Clear();
            travelCache[key] = r;
            return r;
        }

        // =================================================================================== route building

        RouteSnapshot BuildRoute(Vector3 carPos, Vector3 carFwd, Vector3 target)
        {
            travelBudget = maxTravelCallsPerBuild;
            Vector3 dir = Flat(carFwd);
            if (dir.Length() < 0.1f)
                return null;
            dir = Vector3.Normalize(dir);

            Vector3 start; float startHeading;
            if (!FindStartNode(carPos, dir, out start, out startHeading))
                return null;

            Vector3 hd = HeadingDir(startHeading);
            if (Vector3.Dot(hd, dir) < 0f) hd = -hd;
            dir = SafeNormalize(dir * 0.6f + hd * 0.4f);

            // The tracer only ever moves forward, so first check whether the route actually continues
            // behind the car (the game's GPS would say "turn around"). Compare the road distance to the
            // destination from the next node ahead and the next node behind; done first so these
            // pathfinder calls always fit in the budget (they're cached per node anyway).
            bool wrongWay = DetectWrongWay(start, dir, target);
            if (wrongWay)
                dir = -dir;

            List<Vector3> nodes = new List<Vector3>();
            nodes.Add(start);
            Vector3 cur = start;
            float total = 0f;
            bool reached = false;
            int maxSteps = (int)(maxRouteLength / stepLength) + 10;

            for (int i = 0; i < maxSteps; i++)
            {
                if (cur.DistanceTo2D(target) < stepLength * 1.3f)
                {
                    nodes.Add(target);
                    reached = true;
                    break;
                }

                Vector3 next, nextDir;
                if (!StepAlongRoad(cur, dir, target, out next, out nextDir))
                    break;
                if (IsNearRecent(nodes, next, 1.5f))
                    break;

                total += cur.DistanceTo2D(next);
                nodes.Add(next);
                cur = next;
                dir = nextDir;
                if (total >= maxRouteLength)
                    break;
            }

            if (nodes.Count < 2)
                return null;

            for (int i = 0; i < nodes.Count; i++)
            {
                Vector3 n = nodes[i];
                n.Z = GroundZ(n);
                nodes[i] = n;
            }

            List<Vector3> pts = Chaikin(Chaikin(nodes));
            RouteSnapshot r = new RouteSnapshot();
            r.Pts = pts.ToArray();
            r.Cum = new float[r.Pts.Length];
            for (int i = 1; i < r.Pts.Length; i++)
                r.Cum[i] = r.Cum[i - 1] + r.Pts[i - 1].DistanceTo2D(r.Pts[i]);
            r.Length = r.Cum[r.Cum.Length - 1];
            r.EndsAtDestination = reached;
            r.WrongWay = wrongWay;
            r.Id = ++routeIdCounter;
            r.Dest = target;
            r.DestKind = destKind;
            return r;
        }

        // Compares nearby AI cars' driving direction with the heading of the road node under them.
        void SampleTrafficHeadings(Vector3 carPos)
        {
            Vehicle[] cars = World.GetVehicles(carPos, 80f);
            if (cars == null)
                return;
            int used = 0;
            for (int i = 0; i < cars.Length && used < 8; i++)
            {
                Vehicle v = cars[i];
                if (v == null || !Game.Exists(v) || v.Position.DistanceTo(carPos) < 4f || v.Speed < 3f)
                    continue;
                Vector3 node; float h;
                if (!NthNode(v.Position, 1, out node, out h) || node.DistanceTo2D(v.Position) > 6f)
                    continue;
                Vector3 vd = Flat(v.Direction);
                if (vd.Length() < 0.1f)
                    continue;
                float dot = Vector3.Dot(HeadingDir(h), Vector3.Normalize(vd));
                if (Math.Abs(dot) < 0.85f)
                    continue;
                headingEvidence = Math.Max(-60, Math.Min(60, headingEvidence + (dot > 0f ? 1 : -1)));
                used++;
            }
            headingSign = headingEvidence <= -5 ? -1f : 1f;
        }

        bool DetectWrongWay(Vector3 start, Vector3 dir, Vector3 target)
        {
            wrongWayDebug = "wrong-way check: off";
            if (!detectWrongWay || !useTravelDistance)
                return lastWrongWay = false;
            if (start.DistanceTo2D(target) < stepLength * 3f)
                return lastWrongWay = false;

            Vector3 ahead, behind;
            if (!NeighbourAlong(start, dir, dir, out ahead) || !NeighbourAlong(start, -dir, dir, out behind))
            {
                wrongWayDebug = "wrong-way check: no road both ways";
                return lastWrongWay; // e.g. dead end: keep the previous answer
            }

            // these two calls don't count against the junction budget
            int savedBudget = travelBudget;
            travelBudget = int.MaxValue;
            float ta = TravelDistance(ahead, target);
            float tb = TravelDistance(behind, target);
            travelBudget = savedBudget;

            float gap = ahead.DistanceTo2D(behind);
            float la = ahead.DistanceTo2D(target), lb = behind.DistanceTo2D(target);
            wrongWayDebug = string.Format(CultureInfo.InvariantCulture,
                "wrong-way check: ahead {0:0}m (crow {1:0})  behind {2:0}m (crow {3:0})  gap {4:0}m  votes {5}",
                ta, la, tb, lb, gap, wrongWayVotes);

            // A road distance can never be shorter than the straight-line distance; if it is, these numbers
            // aren't real road distances and can't be trusted for this decision.
            if (ta < 0f || tb < 0f || ta < la * 0.95f || tb < lb * 0.95f)
            {
                wrongWayVotes = 0;
                return lastWrongWay = false;
            }

            // Driving the wrong way, the shortest route from the node ahead runs back through the node behind,
            // so it is longer by about the road distance between them. Require most of that difference.
            bool vote = ta - tb > gap * 0.75f;

            // must agree on several rebuilds in a row before flipping either way
            if (vote) wrongWayVotes = Math.Min(wrongWayVotes + 1, 3);
            else wrongWayVotes = Math.Max(wrongWayVotes - 1, 0);
            if (!lastWrongWay && wrongWayVotes >= 3) lastWrongWay = true;
            else if (lastWrongWay && wrongWayVotes <= 1) lastWrongWay = false;
            return lastWrongWay;
        }

        bool NeighbourAlong(Vector3 from, Vector3 dir, Vector3 travelDir, out Vector3 node)
        {
            node = from;
            GatherCandidates(from, dir, stepLength, stepLength * 0.35f, stepLength * 2.2f);
            if (cands.Count == 0)
                return false;
            // stay on the carriageway whose traffic runs along travelDir
            Cand best = cands[0];
            float bestScore = float.MaxValue;
            for (int i = 0; i < cands.Count; i++)
            {
                float with = Vector3.Dot(cands[i].Axis, travelDir) * headingSign;
                float score = Math.Abs(cands[i].Angle) + (with < -0.5f ? 1.5f : 0f);
                if (score < bestScore) { bestScore = score; best = cands[i]; }
            }
            node = best.Pos;
            return true;
        }

        bool FindStartNode(Vector3 carPos, Vector3 dir, out Vector3 best, out float bestHeading)
        {
            best = Vector3.Zero;
            bestHeading = 0f;
            float bestScore = float.MaxValue;
            for (int n = 1; n <= 7; n++)
            {
                Vector3 p; float h;
                if (!NthNode(carPos, n, out p, out h))
                    continue;
                float d = p.DistanceTo2D(carPos);
                if (d > 30f || Math.Abs(p.Z - carPos.Z) > 6f)
                    continue;
                float signed = Vector3.Dot(HeadingDir(h), dir) * headingSign;
                float score = d + (1f - Math.Abs(signed)) * 25f + (signed < -0.5f ? 15f : 0f);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = p;
                    bestHeading = h;
                }
            }
            return bestScore < float.MaxValue;
        }

        bool StepAlongRoad(Vector3 cur, Vector3 dir, Vector3 target, out Vector3 next, out Vector3 nextDir)
        {
            next = cur;
            nextDir = dir;

            GatherCandidates(cur, dir, stepLength, stepLength * 0.35f, stepLength * 2.2f);
            if (cands.Count == 0)
                GatherCandidates(cur, dir, stepLength * 2f, stepLength * 0.5f, stepLength * 3.5f);
            if (cands.Count == 0)
                return false;

            cands.Sort(delegate (Cand a, Cand b) { return a.Angle.CompareTo(b.Angle); });

            // group candidates that lead in roughly the same direction
            List<List<Cand>> clusters = new List<List<Cand>>();
            for (int i = 0; i < cands.Count; i++)
            {
                if (clusters.Count == 0 || cands[i].Angle - cands[i - 1].Angle > 25f * Deg2Rad)
                    clusters.Add(new List<Cand>());
                clusters[clusters.Count - 1].Add(cands[i]);
            }

            Cand chosen;
            if (clusters.Count == 1)
            {
                chosen = BestAligned(clusters[0]);
            }
            else
            {
                // junction: pick the branch with the shortest road distance to the destination
                Cand[] reps = new Cand[clusters.Count];
                float[] travel = new float[clusters.Count];
                bool allValid = true;
                for (int i = 0; i < clusters.Count; i++)
                {
                    reps[i] = BestAligned(clusters[i]);
                    travel[i] = TravelDistance(reps[i].Pos, target);
                    if (float.IsNaN(travel[i]))
                        return false; // out of pathfinder budget: end the route here, extend it next build
                    // a road distance shorter than the straight line isn't a real road distance
                    if (travel[i] < 0f || travel[i] < reps[i].Pos.DistanceTo2D(target) * 0.95f) allValid = false;
                }

                int bestIdx = 0;
                float bestCost = float.MaxValue;
                for (int i = 0; i < reps.Length; i++)
                {
                    float cost = allValid ? reps[i].Dist + travel[i] : reps[i].Pos.DistanceTo2D(target);
                    cost += Math.Abs(reps[i].Angle) * 3f;
                    if (reps[i].DirAlign < -0.5f && reps[i].AxisAlign > 0.7f)
                        cost += 60f; // would mean driving against traffic
                    if (cost < bestCost) { bestCost = cost; bestIdx = i; }
                }
                chosen = reps[bestIdx];
            }

            next = chosen.Pos;
            Vector3 ax = chosen.Axis;
            if (Vector3.Dot(ax, chosen.Dir) < 0f) ax = -ax;
            nextDir = chosen.AxisAlign > 0.6f ? SafeNormalize(chosen.Dir * 0.5f + ax * 0.5f) : chosen.Dir;
            return true;
        }

        void GatherCandidates(Vector3 cur, Vector3 dir, float probeDist, float minD, float maxD)
        {
            cands.Clear();
            Vector3 probe = cur + dir * probeDist;
            for (int n = 1; n <= maxCandidates; n++)
            {
                Vector3 p; float h;
                if (!NthNode(probe, n, out p, out h))
                    continue;
                if (Math.Abs(p.Z - cur.Z) > 6f)
                    continue;

                Vector3 v = Flat(p - cur);
                float d = v.Length();
                if (d < minD || d > maxD)
                    continue;
                Vector3 vn = v / d;
                if (Vector3.Dot(vn, dir) < 0.17f) // more than ~80 degrees off
                    continue;

                bool dup = false;
                for (int i = 0; i < cands.Count; i++)
                    if (cands[i].Pos.DistanceTo2D(p) < 1f) { dup = true; break; }
                if (dup)
                    continue;

                Cand c = new Cand();
                c.Pos = p;
                c.Dir = vn;
                c.Dist = d;
                c.Angle = SignedAngle(dir, vn);
                c.Axis = HeadingDir(h);
                c.AxisAlign = Math.Abs(Vector3.Dot(c.Axis, vn));
                c.DirAlign = Vector3.Dot(c.Axis, vn) * headingSign;
                cands.Add(c);
            }
        }

        Cand BestAligned(List<Cand> list)
        {
            Cand best = list[0];
            float bestScore = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                Cand c = list[i];
                float score = Math.Abs(c.Angle) + Math.Abs(c.Dist - stepLength) / stepLength * 0.4f
                              + (1f - c.AxisAlign) * 0.3f
                              + (c.DirAlign < -0.5f ? 1.5f : 0f); // node on the opposite carriageway
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        static bool IsNearRecent(List<Vector3> nodes, Vector3 p, float radius)
        {
            for (int i = Math.Max(0, nodes.Count - 5); i < nodes.Count; i++)
                if (nodes[i].DistanceTo2D(p) < radius)
                    return true;
            return false;
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

        static void ProjectOnRoute(RouteSnapshot r, Vector3 p, out float s, out float lateral, out float dist)
        {
            s = 0f; lateral = 0f; dist = float.MaxValue;
            Vector3 q = Flat(p);
            for (int i = 0; i < r.Pts.Length - 1; i++)
            {
                Vector3 a = Flat(r.Pts[i]), b = Flat(r.Pts[i + 1]);
                Vector3 ab = b - a;
                float len = ab.Length();
                float t = len > 1e-3f ? Clamp(Vector3.Dot(q - a, ab) / (len * len), 0f, 1f) : 0f;
                Vector3 proj = a + ab * t;
                float d = (q - proj).Length();
                if (d < dist)
                {
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
            float maxDist = MaxAhead(objMaxDistance, st);
            // the extra distance gets extra arrows, so MaxCount keeps the same density at speed
            int maxCount = Math.Min(30, objMaxCount + (int)Math.Ceiling((maxDist - objMaxDistance) / objSpacing));
            float end = Math.Min(s0 + maxDist, r.EndsAtDestination ? r.Length - 3f : r.Length - 1f);

            // An arrow is placed once and then stays exactly where it is. Each frame, find where the
            // existing arrows are on the current route; drop the ones the car has passed, and if the route
            // no longer runs through an arrow (a different turn was taken), drop it and everything after it.
            for (int i = 0; i < placements.Count; i++)
            {
                float ps, plat, pdist;
                ProjectOnRoute(r, placements[i].Road, out ps, out plat, out pdist);
                placements[i].S = pdist < 3f ? ps : float.NaN;
            }
            placements.Sort(delegate (Placement a, Placement b)
            {
                return (float.IsNaN(a.S) ? float.MaxValue : a.S).CompareTo(float.IsNaN(b.S) ? float.MaxValue : b.S);
            });
            float lastS = float.NaN;
            for (int i = 0; i < placements.Count; i++)
            {
                Placement p = placements[i];
                bool keep = !float.IsNaN(p.S)
                            && p.S >= s0 + objMinDistance
                            && p.Road.DistanceTo2D(st.CarPos) >= objMinDistance
                            && p.S <= end + objSpacing
                            && (float.IsNaN(lastS) || p.S - lastS > objSpacing * 0.5f);
                if (!keep)
                {
                    ReleasePlacement(p);
                    placements.RemoveAt(i);
                    i--;
                    continue;
                }
                lastS = p.S;
            }

            // The route's own height is only a straight line between GPS points that can be hundreds of
            // metres apart, so over a crest or dip it can be metres off the road. New arrows look for the
            // road surface near the height of the previous arrow (or the car) instead, following the
            // route's slope from there.
            float refS, refZ;
            if (placements.Count > 0)
            {
                refS = placements[placements.Count - 1].S;
                refZ = placements[placements.Count - 1].Road.Z;
            }
            else
            {
                refS = s0;
                refZ = CarGroundZ(st.CarPos);
            }

            // new arrows always go exactly one spacing after the last one, so spacing can never drift
            float next = float.IsNaN(lastS) ? s0 + objMinDistance + 1f : lastS + objSpacing;
            while (next <= end && placements.Count < maxCount)
            {
                float expectedZ = refZ + PointAt(r, next).Z - PointAt(r, refS).Z;
                bool grounded;
                Placement p = PlaceOnRoad(r, next, expectedZ, out grounded);
                // no road surface found far ahead: collision there isn't streamed in yet, so try again
                // on a later frame rather than leave an arrow floating or buried for good
                if (p != null && !grounded && p.Road.DistanceTo2D(st.CarPos) > 40f)
                    break;
                if (p != null && p.Road.DistanceTo2D(st.CarPos) >= objMinDistance)
                {
                    p.S = next;
                    placements.Add(p);
                    refS = next;
                    refZ = p.Road.Z;
                }
                next += objSpacing;
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
            public Vector3 Road, Up, Pos;
            public float Qx, Qy, Qz, Qw;
            public float S;   // distance along the current route (updated every frame)
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

        static readonly float[] SurfaceProbeHeights = { 1.5f, 4f, 8f, 15f, 30f };

        // Finds the ground surface closest to p.Z. Probing down from several heights finds the road even
        // when p.Z is below it, while picking the closest hit keeps arrows off bridges and overpasses
        // above the road (and off the ground under a bridge the road runs over).
        static bool FindRoadSurface(Vector3 p, out float z)
        {
            z = p.Z;
            float best = 6f;
            bool found = false;
            for (int i = 0; i < SurfaceProbeHeights.Length; i++)
            {
                float g = World.GetGroundZ(new Vector3(p.X, p.Y, p.Z + SurfaceProbeHeights[i]), GroundType.NextBelowCurrent);
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

        // GTA IV heading: degrees, 0 = north (+Y), counter-clockwise
        static Vector3 HeadingDir(float heading)
        {
            float h = heading * Deg2Rad;
            return new Vector3((float)-Math.Sin(h), (float)Math.Cos(h), 0f);
        }

        // positive = counter-clockwise (left)
        static float SignedAngle(Vector3 a, Vector3 b)
        {
            return (float)Math.Atan2(a.X * b.Y - a.Y * b.X, a.X * b.X + a.Y * b.Y);
        }

        static void ParseIntList(string s, HashSet<int> into)
        {
            if (s == null)
                return;
            into.Clear();
            foreach (string part in s.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                    into.Add(v);
            }
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

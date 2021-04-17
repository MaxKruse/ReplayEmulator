using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using OsuParsers.Beatmaps;
using OsuParsers.Beatmaps.Objects;
using OsuParsers.Replays;
using OsuParsers.Enums.Replays;
using System.Numerics;
using OsuParsers.Replays.Objects;

namespace Replay_Emulator
{
    class AssociatedBeatmap
    {
        enum HitState
        {
            Miss = 1 << 0 | Combobreak,
            Combobreak = 1 << 1,
            ThreeHundret = 1 << 2,
            OneHundret = 1 << 3,
            Fifty = 1 << 4,
        }

        private class HitObjectComparer : IComparer<HitObject>
        {
            public int Compare([AllowNull] HitObject x, [AllowNull] HitObject y)
            {
                return x.StartTime.CompareTo(y.StartTime);
            }
        }

        private Beatmap Beatmap { get; set; }
        private Replay Replay { get; set; }
        private SortedDictionary<HitObject, HitState> HitobjectPoints;
        private double Aim_Precision_Factor { get; set; }
        private double Acc_Precision_Factor { get; set; }
        private double HitWindow300 { get; set; }
        private double HitWindow100 { get; set; }
        private double HitWindow50 { get; set; }
        private double HitRadius { get; set; }
        private int TimePreempt { get; set; }
        private double DT_Factor { get; set; }

        public AssociatedBeatmap(Beatmap beatmap, Replay replay)
        {
            HitobjectPoints = new SortedDictionary<HitObject, HitState>(new HitObjectComparer());
            Beatmap = beatmap;
            Replay = replay;

            // Maximal total bonus for the map + replay is 1 + this value
            Aim_Precision_Factor = 0.025;

            // Maximal total bonus for the map + replay is 1 + this value
            Acc_Precision_Factor = 0.025;

            // Adjust for HR
            if ((Replay.Mods & OsuParsers.Enums.Mods.HardRock) != 0)
            {
                // Flip Map
                foreach (var obj in Beatmap.HitObjects)
                {
                    obj.Position = new Vector2(obj.Position.X, 384.0f - obj.Position.Y);
                }

                // Adjust AR, OD and CS for HR by 1.4f up to a max of 10.0f
                Beatmap.DifficultySection.ApproachRate = Math.Min(10.0f, Beatmap.DifficultySection.ApproachRate * 1.4f);
                Beatmap.DifficultySection.OverallDifficulty = Math.Min(10.0f, Beatmap.DifficultySection.OverallDifficulty * 1.4f);
                Beatmap.DifficultySection.CircleSize = Math.Min(10.0f, Beatmap.DifficultySection.CircleSize * 1.3f);
            }

            // Adjust for Easy
            if ((Replay.Mods & OsuParsers.Enums.Mods.Easy) != 0)
            {
                // Adjust OD for EZ by dividing by 2.0f if possible (divide by 0 errors maybe?)
                try
                {
                    Beatmap.DifficultySection.ApproachRate /= 2.0f;
                    Beatmap.DifficultySection.OverallDifficulty /= 2.0f;
                    Beatmap.DifficultySection.CircleSize /= 2.0f;
                }
                catch (Exception) {}
            }

            DT_Factor = 1.0;
            // Adjust for Doubletime/NC
            if ((Replay.Mods & OsuParsers.Enums.Mods.DoubleTime) != 0 || (Replay.Mods & OsuParsers.Enums.Mods.Nightcore) != 0)
            {
                DT_Factor = 1.5;
            }

            // Adjust for HT
            if ((Replay.Mods & OsuParsers.Enums.Mods.HalfTime) != 0)
            {
                DT_Factor = 2.0 / 3.0;
            }

            // Calculate hitwindows
            HitWindow300 = GetHitwindow300();
            HitWindow100 = GetHitwindow100();
            HitWindow50 = GetHitwindow50();
            // Calculation taken from Osu File Formats page
            HitRadius = 54.4 - 4.48 * Beatmap.DifficultySection.CircleSize;

            // Apply stacking as appropriate
            calculateTimePreempt();
            applyStacking();

            // Apply DT to approach rate
            // Beatmap.DifficultySection.ApproachRate = PreemptToApproachRate(TimePreempt / DT_Factor);
            
            // Depending on if we parse a replay, either skip association or dont
            if (Replay.ReplayMD5Hash != null)
            {
                associateHits();
            }
        }

        private float PreemptToApproachRate(double preempt)
        {
            // Taken from https://github.com/ppy/osu/blob/168ba625006f86ca7fd758adcec2e24af1169f40/osu.Game.Rulesets.Osu/Difficulty/OsuDifficultyCalculator.cs#L59
            // MIT Licence
            return (float)(preempt > 1200.0 ? (1800.0 - preempt) / 120.0 : (1200.0 - preempt) / 150.0 + 5.0);
        }

        private void calculateTimePreempt()
        {
            // Taken from the Osu File Formats page
            if (Beatmap.DifficultySection.ApproachRate < 5)
            {
                TimePreempt = (int)(1200 + 600 * (5 - Beatmap.DifficultySection.ApproachRate) / 5);
            }
            else if (Beatmap.DifficultySection.ApproachRate == 5)
            {
                TimePreempt = 1200;
            }
            else if (Beatmap.DifficultySection.ApproachRate > 5)
            {
                TimePreempt = (int)(1200 - 750 * (Beatmap.DifficultySection.ApproachRate - 5) / 5);
            }
        }

        private void associateHits()
        {
            // TODO: Fix detection of 100s, 50s and misses, not working with replays as expected
            ReplayFrame latest = new ReplayFrame();
            var updatedLatest = false;

            // This is the theoretical HitWindow for 50s divided by 2, so normalized from the hitcenter
            var HitWindowEdge = HitWindow50;

            // Iterate through every object
            foreach (var hitobj in Beatmap.HitObjects)
            {
                updatedLatest = false;
                // Get all frames that could affect this object
                var possibleFrames = Replay.ReplayFrames.FindAll((frame) => { return frame.Time >= hitobj.StartTime - HitWindowEdge && frame.Time <= hitobj.StartTime + HitWindowEdge && frame.Time > latest.Time; });
                
                //Check if 1 K2 were pressed
                bool K1_Press = (possibleFrames.First().StandardKeys & StandardKeys.K1) != 0; // K1 includes M1 as they mutually block
                bool K2_Press = (possibleFrames.First().StandardKeys & StandardKeys.K2) != 0; // K2 includes M2 as they mutually block

                // Assume a miss unless we hit something later
                HitState hitState = HitState.Miss;

                // check for first frame that hits this hitobject
                foreach (var frame in possibleFrames)
                {
                    if (hitobj is HitCircle)
                    {
                        // For circles we want to check if we are actually hovering over it on this frame, else we cant hit it at all
                        if (CursorOverCircle(hitobj, new Vector2(frame.X, frame.Y)))
                        {
                            // Check if we clicked on this frame, but didnt have the key pressed before (we received keydown on this frame)
                            if (K1_Press == false && (frame.StandardKeys & StandardKeys.K1) != 0)
                            {
                                hitState = resolveHitstate(hitobj, frame);
                                latest = frame;
                                updatedLatest = true;
                                break;
                            }
                            if (K2_Press == false && (frame.StandardKeys & StandardKeys.K2) != 0)
                            {
                                hitState = resolveHitstate(hitobj, frame);
                                latest = frame;
                                updatedLatest = true;
                                break;
                            }
                        }
                        // get states
                        K1_Press = (frame.StandardKeys & StandardKeys.K1) != 0;
                        K2_Press = (frame.StandardKeys & StandardKeys.K2) != 0;
                    }
                    else if (hitobj is Slider)
                    {
                        // We only check if we hover over the sliderhead right now, which isnt good enough. Need to also check for sliderticks and sliderend
                        if (CursorOverCircle(hitobj, new Vector2(frame.X, frame.Y)))
                        {
                            // Check if we clicked on this frame, but didnt have the key pressed before (we received keydown on this frame)
                            if (K1_Press == false && (frame.StandardKeys & StandardKeys.K1) != 0)
                            {
                                hitState = HitState.ThreeHundret;
                                latest = frame;
                                updatedLatest = true;
                                break;
                            }
                            if (K2_Press == false && (frame.StandardKeys & StandardKeys.K2) != 0)
                            {
                                hitState = HitState.ThreeHundret;
                                latest = frame;
                                updatedLatest = true;
                                break;
                            }
                        }
                        // get states
                        K1_Press = (frame.StandardKeys & StandardKeys.K1) != 0;
                        K2_Press = (frame.StandardKeys & StandardKeys.K2) != 0;
                    }
                    else
                    {
                        // Spinners shouldnt affect pp at all, lmao.
                        hitState = HitState.ThreeHundret;
                    }
                }
                if (!updatedLatest)
                {
                    latest = possibleFrames.Last();
                }
                // only add if we can
                if (!HitobjectPoints.ContainsKey(hitobj))
                    HitobjectPoints.Add(hitobj, hitState);
            }
        }

        private double GetHitwindow50()
        {
            return (400.0 - 20.0 * Beatmap.DifficultySection.OverallDifficulty) / 2.0;
        }

        private double GetHitwindow100()
        {
            return (280.0 - 16.0 * Beatmap.DifficultySection.OverallDifficulty) / 2.0;
        }

        private double GetHitwindow300()
        {
            return (160.0 - 12.0 * Beatmap.DifficultySection.OverallDifficulty) / 2.0;
        }

        private bool CursorOverCircle(HitObject obj, Vector2 vector2D)
        {
            return Vector2.Distance(obj.Position, vector2D) <= (float)HitRadius;
        }

        private HitState resolveHitstate(HitObject hitobj, ReplayFrame frame)
        {
            var hitWindowHit = Math.Abs(hitobj.StartTime - frame.Time);
            if (hitWindowHit <= HitWindow300) return HitState.ThreeHundret;
            if (hitWindowHit <= HitWindow100) return HitState.OneHundret;
            if (hitWindowHit <= HitWindow50) return HitState.Fifty;

            return HitState.Miss;
        }

        private void applyStacking()
        {
            // implementation from https://gist.github.com/peppy/1167470
            int stack_distance = (int)Math.Ceiling(HitRadius / 10.0);
            var stackVector = new Vector2(stack_distance, stack_distance);

            int extendedStartIndex = 0;


            int STACK_LENIENCY = (int)(Beatmap.GeneralSection.StackLeniency * 10);
            double stackThreshold = TimePreempt * STACK_LENIENCY / 10;

            Dictionary<int, int> stackMap = new Dictionary<int, int>();

            foreach (var obj in Beatmap.HitObjects)
            {
                stackMap[obj.StartTime] = 0;
            }

            for (int i = Beatmap.HitObjects.Count - 1; i > 0; i--)
            {
                int n = i;

                HitObject objectI = Beatmap.HitObjects[i];
                if (stackMap[objectI.StartTime] != 0 || objectI is Spinner) continue;

                if (objectI is HitCircle)
                {
                    while (--n >= 0)
                    {
                        HitObject objectN = Beatmap.HitObjects[n];
                        if (objectN is Spinner) continue;

                        double endTime = objectN.EndTime;

                        if (objectI.StartTime - stackThreshold > endTime)
                            // We are no longer within stacking range of the previous object.
                            break;

                        // HitObjects before the specified update range haven't been reset yet
                        if (n < extendedStartIndex)
                        {
                            stackMap[objectN.StartTime] = 0;
                            extendedStartIndex = n;
                        }

                        /* This is a special case where hticircles are moved DOWN and RIGHT (negative stacking) if they are under the *last* slider in a stacked pattern.
                            *    o==o <- slider is at original location
                            *        o <- hitCircle has stack of -1
                            *         o <- hitCircle has stack of -2
                            */
                        if (objectN is Slider && Vector2.Distance(objectN.Position, objectI.Position) < stack_distance)
                        {
                            int offset = stackMap[objectI.StartTime] - stackMap[objectN.StartTime] + 1;

                            for (int j = n + 1; j <= i; j++)
                            {
                                // For each object which was declared under this slider, we will offset it to appear *below* the slider end (rather than above).
                                HitObject objectJ = Beatmap.HitObjects[j];
                                if (Vector2.Distance(objectN.Position, objectJ.Position) < stack_distance)
                                    stackMap[objectJ.StartTime] -= offset;
                            }

                            // We have hit a slider.  We should restart calculation using this as the new base.
                            // Breaking here will mean that the slider still has StackCount of 0, so will be handled in the i-outer-loop.
                            break;
                        }

                        if (Vector2.Distance(objectN.Position, objectI.Position) < stack_distance)
                        {
                            // Keep processing as if there are no sliders.  If we come across a slider, this gets cancelled out.
                            //NOTE: Sliders with start positions stacking are a special case that is also handled here.

                            stackMap[objectN.StartTime] = stackMap[objectI.StartTime] + 1;
                            objectI = objectN;
                        }
                    }
                }
                else if (objectI is Slider)
                {
                    /* We have hit the first slider in a possible stack.
                        * From this point on, we ALWAYS stack positive regardless.
                        */
                    while (--n >= 0)
                    {
                        HitObject objectN = Beatmap.HitObjects[n];
                        if (objectN is Spinner) continue;

                        if (objectI.StartTime - stackThreshold > objectN.StartTime)
                            // We are no longer within stacking range of the previous object.
                            break;

                        if (Vector2.Distance(objectN.Position, objectI.Position) < STACK_LENIENCY)
                        {
                            stackMap[objectN.StartTime] = stackMap[objectI.StartTime] + 1;
                            objectI = objectN;
                        }
                    }
                }
            }

            // apply the stacking
            foreach (var obj in Beatmap.HitObjects)
            {
                obj.Position -= stackVector * stackMap[obj.StartTime];
            }

            int k = 0;
            foreach (var stack in stackMap)
            {
                if (stack.Value > 0)
                {
                    k++;
                }
            }
        }
        public double GetAimPrecision()
        {
            return 1.0 + Aim_Precision_Factor;
        }

        public double GetAccPrecision()
        {
            return 1.0 + Acc_Precision_Factor;
        }
    }
}

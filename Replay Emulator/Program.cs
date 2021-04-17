using System;
using System.Collections.Generic;
using CommandLine;
using OsuParsers.Beatmaps;
using OsuParsers.Replays;
using OsuParsers.Decoders;
using System.Diagnostics;
using OsuParsers.Enums;

namespace Replay_Emulator
{
    class Program
    {
        class Options
        {
            [Option('b', "beatmap", Required = true, HelpText = ".osu Beatmap file")]
            public string BeatmapPath { get; set; }

            [Option('r', "replay", Required = true, HelpText = ".osr Replay file")]
            public string ReplayPath { get; set; }
        }

        static void Main(string[] args)
        {
            CommandLine.Parser.Default.ParseArguments<Options>(args)
                .WithParsed(RunOptions)
                .WithNotParsed(HandleParseError);
        }
        static void RunOptions(Options opts)
        {
            // Parse Beatmap and replay
            var beatmap = new Beatmap();
            var replay = new Replay();
            try
            {
                beatmap = BeatmapDecoder.Decode(opts.BeatmapPath);
            }
            catch (Exception)
            {
                Console.WriteLine("Can not parse Beatmap " + opts.BeatmapPath);
                return;
            }

            if (opts.ReplayPath != null)
            {
                try
                {
                    replay = ReplayDecoder.Decode(opts.ReplayPath);
                    if (replay.Mods == OsuParsers.Enums.Mods.Relax) return;
                }
                catch (Exception)
                {
                    Console.WriteLine("Can not parse Replay " + opts.ReplayPath);
                    return;
                }
            }

            var associated = new AssociatedBeatmap(beatmap, replay);
            Console.WriteLine("Aim: {0:R}", associated.GetAimPrecision());
            Console.WriteLine("Acc: {0:R}", associated.GetAccPrecision());
            return;
        }
        static void HandleParseError(IEnumerable<Error> errs)
        {
            Console.WriteLine("Invalid Arguments");
            foreach (var error in errs)
            {
                Console.WriteLine(error.ToString());
            }
        }

    }
}

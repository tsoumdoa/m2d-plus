using M2DPlus.HcPerf;
if (args[0] == "--rigid") Gates.RoundedRigid(args[1], args[2], args[3]);
else if (args[0] == "--census") Gates.Census(args[1], args[2]);
else if (args[0] == "--fable") Console.WriteLine(Gates.Fable(args[1]));
else if (args[0] == "--compare") Console.WriteLine(Gates.CompareFiles(args[1], args[2], double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture), double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)));
else if (args[0] == "--tiles") Gates.Tiles(args[1], args[2], args[3]);
else Bench.Run(args[0], args[1], args[2], args.Contains("--oracle"), false, 3, 8);

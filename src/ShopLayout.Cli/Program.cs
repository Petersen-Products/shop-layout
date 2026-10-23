using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using ShopLayout.Core.Config;
using ShopLayout.Core.Export;
using ShopLayout.Core.Model;
using ShopLayout.SolidWorks;

namespace ShopLayout.Cli
{
    internal static class Program
    {
        private const int ExitOk = 0, ExitWarnings = 1, ExitErrors = 2, ExitFatal = 3, ExitUsage = 4;

        private const string Usage = @"ShopLayoutExport - export a SolidWorks shop floor assembly to shop_layout.json + equipment_library.json

Usage:
  ShopLayoutExport validate [options]   Dry run: classify, measure and report; writes no layout files.
  ShopLayoutExport export   [options]   Full export to --out.
  ShopLayoutExport init-config [--out <file>]   Write the sample configuration file.
  ShopLayoutExport drawio --from-snapshot <file> [--cleanup <file>] [--out <dir>] [--name <base>]
                                        One-time import: snapshot -> <base>.drawio, <base>_library.xml, <base>_import_log.md.
  ShopLayoutExport sanitize-snapshot --from-snapshot <in> --out <out>
                                        Strip drive roots and folders from paths so a snapshot can be shared.

Options:
  --config <file>        Configuration (default: export.config.json in the current folder, then next to the exe).
  --out <dir>            Output folder for export (default: .\shop_layout_export).
  --report <file>        Also write the validation report JSON here (validate mode).
  --floor <plane>        Override floor: XY | XZ | YZ | plane:<reference plane name>.
  --no-resolve           Never resolve lightweight components.
  --allow-resolve        validate only: allow temporary lightweight resolution for exact dimensions.
  --record <file>        Save a snapshot of everything read from SolidWorks (for offline re-runs).
  --from-snapshot <file> Run from a snapshot instead of SolidWorks.
  --progid <id>          COM ProgID (default SldWorks.Application; e.g. SldWorks.Application.33).
  --strict               Exit code 1 when there are warnings.
  --quiet                Print only the summary line.

Exit codes: 0 ok, 1 warnings (--strict), 2 errors, 3 fatal, 4 usage.
SolidWorks must already be running with the assembly active. The model is never saved.";

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0 || IsHelp(args[0])) { Console.WriteLine(Usage); return args.Length == 0 ? ExitUsage : ExitOk; }

            string command = args[0].ToLowerInvariant();
            var opts = ParseOptions(args, 1, out string error);
            if (error != null) { Console.Error.WriteLine(error); Console.Error.WriteLine("Run with --help for usage."); return ExitUsage; }

            try
            {
                switch (command)
                {
                    case "init-config": return InitConfig(opts);
                    case "validate": return Run(opts, dryRun: true);
                    case "export": return Run(opts, dryRun: false);
                    case "drawio": return Drawio(opts);
                    case "sanitize-snapshot": return SanitizeSnapshot(opts);
                    default:
                        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
                        Console.Error.WriteLine(Usage);
                        return ExitUsage;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return ExitFatal;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("UNEXPECTED ERROR: " + ex);
                return ExitFatal;
            }
        }

        private static bool IsHelp(string a) => a == "-h" || a == "--help" || a == "/?" || a.Equals("help", StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, string> ParseOptions(string[] args, int start, out string error)
        {
            var flags = new HashSet<string> { "--no-resolve", "--allow-resolve", "--strict", "--quiet" };
            var withValue = new HashSet<string> { "--config", "--out", "--report", "--floor", "--record", "--from-snapshot", "--progid", "--cleanup", "--name" };
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            for (int i = start; i < args.Length; i++)
            {
                string a = args[i];
                if (flags.Contains(a)) d[a] = "true";
                else if (withValue.Contains(a))
                {
                    if (i + 1 >= args.Length) { error = $"Option {a} needs a value."; return d; }
                    d[a] = args[++i];
                }
                else { error = $"Unknown option '{a}'."; return d; }
            }
            return d;
        }

        private static int InitConfig(Dictionary<string, string> opts)
        {
            string target = opts.TryGetValue("--out", out var o) ? o : "export.config.json";
            string sample = Path.Combine(ExeDir(), "export.config.json");
            if (!File.Exists(sample)) throw new IOException("The sample export.config.json is missing next to the executable.");
            if (File.Exists(target)) { Console.Error.WriteLine($"{target} already exists; not overwritten."); return ExitUsage; }
            File.Copy(sample, target);
            Console.WriteLine($"Wrote {Path.GetFullPath(target)}");
            return ExitOk;
        }

        private static string ExeDir() => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        private static int SanitizeSnapshot(Dictionary<string, string> opts)
        {
            if (!opts.TryGetValue("--from-snapshot", out var input) || !opts.TryGetValue("--out", out var output))
            {
                Console.Error.WriteLine("sanitize-snapshot needs --from-snapshot <file> and --out <file>.");
                return ExitUsage;
            }
            string clean = SnapshotIo.Sanitize(File.ReadAllText(input, Encoding.UTF8), out int n);
            File.WriteAllText(output, clean, new UTF8Encoding(false));
            Console.WriteLine($"Rewrote {n} path(s); wrote {Path.GetFullPath(output)}");
            return ExitOk;
        }

        private static int Drawio(Dictionary<string, string> opts)
        {
            if (!opts.TryGetValue("--from-snapshot", out var snap))
            {
                Console.Error.WriteLine("drawio needs --from-snapshot <file> (the import runs from the recorded SolidWorks data).");
                return ExitUsage;
            }
            var cfg = LoadConfig(opts, out string cfgFrom);
            string cleanupPath = opts.TryGetValue("--cleanup", out var cp) ? cp
                : File.Exists("import_cleanup.json") ? "import_cleanup.json"
                : Path.Combine(ExeDir(), "import_cleanup.json");
            if (!File.Exists(cleanupPath)) throw new IOException($"Cleanup file not found: {cleanupPath}");
            var cleanup = ShopLayout.Drawio.ImportCleanup.Load(cleanupPath);
            bool quiet = opts.ContainsKey("--quiet");
            if (!quiet) Console.Error.WriteLine($"Snapshot: {Path.GetFullPath(snap)}\nConfig: {cfgFrom}\nCleanup: {Path.GetFullPath(cleanupPath)}");

            var res = new ExportPipeline(new SnapshotSource(snap), cfg, new ExportOptions
            {
                ExporterVersion = typeof(Program).Assembly.GetName().Version.ToString(3),
                Progress = s => { if (!quiet) Console.Error.WriteLine("  " + s); }
            }).Run();
            if (res.Fatal) { Console.Error.WriteLine(res.ReportText); return ExitFatal; }

            var model = new ShopLayout.Drawio.LayoutImporter(res, cleanup).Build();
            string outDir = opts.TryGetValue("--out", out var o) ? o : "diagrams";
            string name = opts.TryGetValue("--name", out var n) ? n : "layout";
            Directory.CreateDirectory(outDir);
            var utf8 = new UTF8Encoding(false);
            var files = new[]
            {
                (Path.Combine(outDir, name + ".drawio"), ShopLayout.Drawio.DrawioWriter.WriteDiagram(model)),
                (Path.Combine(outDir, name + "_library.xml"), ShopLayout.Drawio.DrawioWriter.WriteLibrary(model)),
                (Path.Combine(outDir, name + "_import_log.md"), ShopLayout.Drawio.DrawioWriter.WriteLog(model)),
            };
            foreach (var (path, text) in files)
            {
                File.WriteAllText(path, text, utf8);
                Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
            }
            foreach (var s in model.Log.Summary) Console.WriteLine("  " + s);
            return res.ErrorCount > 0 ? ExitErrors : ExitOk;
        }

        private static ExportConfig LoadConfig(Dictionary<string, string> opts, out string from)
        {
            string path = null;
            if (opts.TryGetValue("--config", out var p))
            {
                if (!File.Exists(p)) throw new IOException($"Config file not found: {p}");
                path = p;
            }
            else if (File.Exists("export.config.json")) path = "export.config.json";
            else if (File.Exists(Path.Combine(ExeDir(), "export.config.json"))) path = Path.Combine(ExeDir(), "export.config.json");

            ExportConfig cfg;
            try { cfg = path != null ? ExportConfig.Load(path) : new ExportConfig(); }
            catch (FormatException ex) { throw new FormatException($"Config file {path}: {ex.Message}"); }
            from = path != null ? Path.GetFullPath(path) : "(built-in defaults)";

            if (opts.TryGetValue("--floor", out var floor))
            {
                if (floor.StartsWith("plane:", StringComparison.OrdinalIgnoreCase))
                {
                    cfg.Floor.Plane = "referencePlane";
                    cfg.Floor.ReferencePlaneName = floor.Substring(6);
                }
                else cfg.Floor.Plane = floor;
            }
            if (opts.ContainsKey("--no-resolve")) cfg.Geometry.ResolveLightweight = false;
            return cfg;
        }

        private static int Run(Dictionary<string, string> opts, bool dryRun)
        {
            var cfg = LoadConfig(opts, out string cfgFrom);
            bool quiet = opts.ContainsKey("--quiet");
            void Progress(string s) { if (!quiet) Console.Error.WriteLine("  " + s); }

            IAssemblySource source;
            if (opts.TryGetValue("--from-snapshot", out var snap))
                source = new SnapshotSource(snap);
            else
            {
                Progress("Connecting to SolidWorks…");
                source = SwConnection.ConnectToActiveAssembly(opts.TryGetValue("--progid", out var pid) ? pid : SwConnection.DefaultProgId, new SwSourceOptions
                {
                    RestoreLightweight = cfg.Geometry.RestoreLightweight,
                    FrameMatchTolerance = cfg.Geometry.FrameMatchTolerance,
                    ArcChordTolerance = cfg.Geometry.ArcChordTolerance,
                    Progress = Progress
                });
            }
            RecordingSource recorder = null;
            if (opts.TryGetValue("--record", out var recordPath)) source = recorder = new RecordingSource(source);

            if (!quiet) Console.Error.WriteLine($"Source: {source.Description}\nConfig: {cfgFrom}");

            var pipeline = new ExportPipeline(source, cfg, new ExportOptions
            {
                DryRun = dryRun,
                AllowResolveInDryRun = opts.ContainsKey("--allow-resolve"),
                ExporterVersion = typeof(Program).Assembly.GetName().Version.ToString(3),
                Progress = Progress
            });
            var res = pipeline.Run();

            if (recorder != null)
            {
                recorder.Save(recordPath);
                Progress($"Snapshot written to {Path.GetFullPath(recordPath)}");
            }

            if (!quiet) Console.WriteLine(res.ReportText);

            if (!dryRun && !res.Fatal)
            {
                string outDir = opts.TryGetValue("--out", out var o) ? o : "shop_layout_export";
                Directory.CreateDirectory(outDir);
                var utf8 = new UTF8Encoding(false);
                File.WriteAllText(Path.Combine(outDir, cfg.Output.LayoutFile), res.LayoutJson, utf8);
                File.WriteAllText(Path.Combine(outDir, cfg.Output.LibraryFile), res.LibraryJson, utf8);
                File.WriteAllText(Path.Combine(outDir, cfg.Output.ReportFile), res.ReportJson, utf8);
                Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, cfg.Output.LayoutFile))}");
                Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, cfg.Output.LibraryFile))}");
                Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, cfg.Output.ReportFile))}");
            }
            else if (opts.TryGetValue("--report", out var reportPath))
            {
                File.WriteAllText(reportPath, res.ReportJson, new UTF8Encoding(false));
                Console.WriteLine($"Wrote {Path.GetFullPath(reportPath)}");
            }

            Console.WriteLine($"{(dryRun ? "Validation" : "Export")} finished: {res.ErrorCount} error(s), {res.WarningCount} warning(s). layout {res.Layout?["contentHash"]}");
            if (res.Fatal) return ExitFatal;
            if (res.ErrorCount > 0) return ExitErrors;
            if (res.WarningCount > 0 && opts.ContainsKey("--strict")) return ExitWarnings;
            return ExitOk;
        }
    }
}

// micromound-acceptance — the Generic Physical Mound acceptance sequence, run in software.
//
// docs/ROADMAP.md states the acceptance target as criteria rather than a milestone: what a minimal
// bench must be seen to do, in order, before MicroMound is a functional physical edge colony. This
// runs that sequence against a real mound — real kernel, real ants, real drivers, real durable
// store, real signed wire — and, when `make -C firmware/micromound-c tools` has been run, against
// the real port-server firmware in its own process on the other end of a real byte stream.
//
// It is not a substitute for the bench. It is the thing that makes the bench day short: every
// criterion that can be met without a soldering iron is met here first, in a deterministic run, and
// what remains on the bench day is the wiring.
//
//   micromound-acceptance                 both legs: the firmware if it is built, and in-memory ports
//   micromound-acceptance --in-memory     only the in-memory leg (no C toolchain needed)
//   micromound-acceptance --board <path>  a specific mm_board_sim binary
//   micromound-acceptance --verbose       trace each criterion as it is checked
//
// Exit code 0 when every applicable criterion is met, 1 otherwise.

using Micromound.Acceptance;

var verbose = args.Contains("--verbose", StringComparer.Ordinal);
var inMemoryOnly = args.Contains("--in-memory", StringComparer.Ordinal);
var boardIndex = Array.IndexOf(args, "--board");
var boardBinary = boardIndex >= 0 && boardIndex + 1 < args.Length ? args[boardIndex + 1] : BoardProcess.FindBinary();

if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("usage: micromound-acceptance [--in-memory] [--board <mm_board_sim>] [--verbose]");
    return 0;
}

Console.WriteLine("MICROMOUND — Generic Physical Mound acceptance (docs/ACCEPTANCE.md)");
Console.WriteLine();

var legs = new List<(string Name, string How)>();
if (!inMemoryOnly && boardBinary is not null) legs.Add(("firmware", boardBinary));
else if (!inMemoryOnly)
    Console.WriteLine("  note: firmware/micromound-c/build/mm_board_sim is not built, so the firmware leg is skipped.\n" +
                      "        build it with `make -C firmware/micromound-c tools` (a C compiler and make).\n");
legs.Add(("in-memory", ""));

var failed = 0;
foreach (var (name, how) in legs)
{
    var root = Path.Combine(Path.GetTempPath(), "mm-acceptance-" + Guid.NewGuid().ToString("N"));
    BoardProcess? board = null;
    try
    {
        if (name == "firmware")
        {
            board = BoardProcess.Start(how);
            Console.WriteLine($"── leg: the port-server firmware in its own process ─────────────────────────");
            Console.WriteLine($"   {how}");
            Console.WriteLine($"   {board.Banner}");
        }
        else
        {
            Console.WriteLine($"── leg: in-memory ports (no firmware, no link) ──────────────────────────────");
        }
        Console.WriteLine();

        var run = new AcceptanceRun(board, root, verbose ? Console.Error.WriteLine : null);
        run.Run();

        foreach (var criterion in run.Criteria)
        {
            var mark = criterion.Verdict switch
            {
                Verdict.Met => "ok  ",
                Verdict.Failed => "FAIL",
                _ => "n/a "
            };
            Console.WriteLine($"  {criterion.Number,2}  {mark}  {criterion.Name}");
            Console.WriteLine($"          {criterion.Detail}");
        }

        var met = run.Criteria.Count(c => c.Verdict == Verdict.Met);
        var skipped = run.Criteria.Count(c => c.Verdict == Verdict.NotApplicable);
        var unmet = run.Criteria.Count(c => c.Verdict == Verdict.Failed);
        failed += unmet;
        Console.WriteLine();
        Console.WriteLine($"  {run.Criteria.Count} criteria — {met} met, {unmet} unmet, {skipped} not applicable to this leg");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"  the {name} leg could not run: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine();
    }
    finally
    {
        board?.Dispose();
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { /* a temp dir */ }
    }
}

Console.WriteLine(failed == 0
    ? "==== ACCEPTANCE SEQUENCE MET IN SOFTWARE ====\n" +
      "     What remains is the bench: the same sequence over real wiring, on a Pi and a flashed board."
    : $"==== {failed} CRITERION(S) UNMET ====");
return failed == 0 ? 0 : 1;

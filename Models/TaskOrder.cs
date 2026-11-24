using System.Globalization;
using Pulse.Telemetry;

namespace Pulse.Models;

internal enum SortField { Name, Pid, Cpu, Ram, Io, Threads }

// The combined counters of a process and all of its descendants.
internal readonly record struct BranchTotal(int Processes, double? Cpu, long WorkingSet, double? Io, int Threads);

// Total is set for a collapsed branch, which stands in for the processes hidden below it.
internal readonly record struct Branch(TaskSample Task, int Depth, bool HasChildren, BranchTotal? Total = null);

internal static class TaskOrder
{
    public const string PidPrefix = "pid:";

    private readonly record struct Metrics(TaskSample Task, double? Cpu, long WorkingSet, double? Io, int Threads);

    public static IEnumerable<Branch> Build(IReadOnlyList<TaskSample> source, string search, SortField sort,
        bool descending, bool tree, HashSet<ProcessKey> collapsed)
    {
        search = search.Trim();
        // "pid:1234" names one process exactly. Other tabs use it to point at the process behind a row.
        int? exact = search.StartsWith(PidPrefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(search.AsSpan(PidPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int wanted) ? wanted : null;
        bool Match(TaskSample t) => exact is { } pid ? t.Info.Key.Pid == pid :
            t.Info.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            t.Info.Key.Pid.ToString().Contains(search, StringComparison.Ordinal);

        var totals = new Dictionary<ProcessKey, BranchTotal>();
        // Rows are ordered by the values they display, so a collapsed branch sorts by its totals.
        Metrics Shown(TaskSample t) => totals.TryGetValue(t.Info.Key, out var total)
            ? new Metrics(t, total.Cpu, total.WorkingSet, total.Io, total.Threads)
            : new Metrics(t, t.Cpu, t.Info.WorkingSet, t.Io, t.Info.Threads);
        int Compare(TaskSample first, TaskSample second)
        {
            Metrics a = Shown(first), b = Shown(second);
            int result = sort switch
            {
                SortField.Name => StringComparer.OrdinalIgnoreCase.Compare(a.Task.Info.Name, b.Task.Info.Name),
                SortField.Pid => a.Task.Info.Key.Pid.CompareTo(b.Task.Info.Key.Pid),
                SortField.Cpu => Nullable.Compare(a.Cpu, b.Cpu),
                SortField.Ram => a.WorkingSet.CompareTo(b.WorkingSet),
                SortField.Io => Nullable.Compare(a.Io, b.Io),
                _ => a.Threads.CompareTo(b.Threads)
            };
            if (sort is SortField.Cpu or SortField.Io)
            {
                bool missingA = (sort == SortField.Cpu ? a.Cpu : a.Io) is null;
                bool missingB = (sort == SortField.Cpu ? b.Cpu : b.Io) is null;
                if (missingA != missingB) return missingA ? 1 : -1;
            }
            return result == 0 ? a.Task.Info.Key.Pid.CompareTo(b.Task.Info.Key.Pid) : descending ? -result : result;
        }

        if (!tree)
        {
            var sorted = source.ToList();
            sorted.Sort(Compare);
            return sorted.Where(Match).Select(t => new Branch(t, 0, false));
        }

        var byPid = source.ToDictionary(t => t.Info.Key.Pid);
        var children = new Dictionary<int, List<TaskSample>>();
        foreach (var t in source)
        {
            int parent = ValidParent(t, byPid)?.Info.Key.Pid ?? 0;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(t);
        }

        // A search expands every branch, so totals only apply without one.
        if (search.Length == 0)
            foreach (var t in source)
                if (collapsed.Contains(t.Info.Key) && children.ContainsKey(t.Info.Key.Pid))
                    totals[t.Info.Key] = Sum(t, children);
        foreach (var list in children.Values) list.Sort(Compare);

        var visible = new HashSet<ProcessKey>();
        if (search.Length != 0)
        {
            foreach (var match in source.Where(Match))
                for (TaskSample? p = match; p != null && visible.Add(p.Info.Key); p = ValidParent(p, byPid)) { }
        }

        var result = new List<Branch>();
        var pending = new Stack<(TaskSample Task, int Depth)>();
        void Push(int parent, int depth)
        {
            if (!children.TryGetValue(parent, out var list)) return;
            for (int i = list.Count - 1; i >= 0; i--) pending.Push((list[i], depth));
        }
        Push(0, 0);
        while (pending.TryPop(out var node))
        {
            var key = node.Task.Info.Key;
            if (search.Length != 0 && !visible.Contains(key)) continue;
            BranchTotal? total = totals.TryGetValue(key, out var sum) ? sum : null;
            result.Add(new(node.Task, node.Depth, children.ContainsKey(key.Pid), total));
            if (search.Length != 0 || !collapsed.Contains(key)) Push(key.Pid, node.Depth + 1);
        }
        return result;
    }

    private static BranchTotal Sum(TaskSample root, Dictionary<int, List<TaskSample>> children)
    {
        int processes = 0, threads = 0;
        long workingSet = 0;
        double? cpu = null, io = null;
        var pending = new Stack<TaskSample>();
        pending.Push(root);
        while (pending.TryPop(out var task))
        {
            processes++;
            threads += task.Info.Threads;
            workingSet += task.Info.WorkingSet;
            cpu = Add(cpu, task.Cpu);
            io = Add(io, task.Io);
            if (children.TryGetValue(task.Info.Key.Pid, out var list))
                foreach (var child in list) pending.Push(child);
        }
        return new(processes, cpu is { } c ? Math.Min(c, 100) : null, workingSet, io, threads);
    }

    // Unlike the lifted operator, a missing value does not erase the other one.
    private static double? Add(double? a, double? b) => a is null ? b : b is null ? a : a + b;

    // A reused parent PID can belong to a process started after the child.
    public static TaskSample? ValidParent(TaskSample task, IReadOnlyDictionary<int, TaskSample> byPid) =>
        byPid.TryGetValue(task.Info.ParentId, out var parent) && parent.Info.Key.Created < task.Info.Key.Created ? parent : null;
}

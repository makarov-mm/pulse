using System.Globalization;
using Pulse.Telemetry;

namespace Pulse.Models;

internal enum SortField { Name, Pid, Cpu, Ram, Io, Threads, Gpu }

internal readonly record struct BranchTotal(int Processes, double? Cpu, long WorkingSet, double? Io, int Threads);

internal readonly record struct Branch(TaskSample Task, int Depth, bool HasChildren, BranchTotal? Total = null);

[System.Reflection.Obfuscation(Exclude = false, ApplyToMembers = true)]
internal static class TaskOrder
{
    public const string PidPrefix = "pid:";
    private readonly record struct Metrics(TaskSample Task, double? Cpu, long WorkingSet, double? Io, int Threads);

    public static IEnumerable<Branch> Build(IReadOnlyList<TaskSample> source, string search, SortField sort, bool descending, bool tree, HashSet<ProcessKey> collapsed)
    {
        search = search.Trim();

        int? exact = search.StartsWith(PidPrefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(search.AsSpan(PidPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int wanted) ? wanted : null;

        bool Match(TaskSample t) => exact is { } pid ? t.Info.Key.Pid == pid :
            t.Info.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            t.Info.Key.Pid.ToString().Contains(search, StringComparison.Ordinal);

        var totals = new Dictionary<ProcessKey, BranchTotal>();

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
                SortField.Gpu => Nullable.Compare(a.Task.Gpu?.Percent, b.Task.Gpu?.Percent),
                SortField.Cpu => Nullable.Compare(a.Cpu, b.Cpu),
                SortField.Ram => a.WorkingSet.CompareTo(b.WorkingSet),
                SortField.Io => Nullable.Compare(a.Io, b.Io),
                _ => a.Threads.CompareTo(b.Threads)
            };

            if (sort is SortField.Cpu or SortField.Io or SortField.Gpu)
            {
                bool missingA = (sort == SortField.Cpu ? a.Cpu : sort == SortField.Gpu ? a.Task.Gpu?.Percent : a.Io) is null;
                bool missingB = (sort == SortField.Cpu ? b.Cpu : sort == SortField.Gpu ? b.Task.Gpu?.Percent : b.Io) is null;
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

        foreach (TaskSample t in source)
        {
            int parent = ValidParent(t, byPid)?.Info.Key.Pid ?? 0;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(t);
        }

        if (search.Length == 0)
            foreach (TaskSample t in source)
                if (collapsed.Contains(t.Info.Key) && children.ContainsKey(t.Info.Key.Pid))
                    totals[t.Info.Key] = Sum(t, children);

        foreach (List<TaskSample> list in children.Values)
        {
            list.Sort(Compare);
        }

        HashSet<ProcessKey> visible = [];

        if (search.Length != 0)
        {
            foreach (TaskSample match in source.Where(Match))
            {
                for (TaskSample? p = match; p != null && visible.Add(p.Info.Key); p = ValidParent(p, byPid))
                {

                }
            }
        }

        var result = new List<Branch>();
        var pending = new Stack<(TaskSample Task, int Depth)>();

        Push(0, 0);

        while (pending.TryPop(out (TaskSample Task, int Depth) node))
        {
            var key = node.Task.Info.Key;
            if (search.Length != 0 && !visible.Contains(key)) continue;
            BranchTotal? total = totals.TryGetValue(key, out var sum) ? sum : null;
            result.Add(new(node.Task, node.Depth, children.ContainsKey(key.Pid), total));
            if (search.Length != 0 || !collapsed.Contains(key)) Push(key.Pid, node.Depth + 1);
        }

        return result;

        void Push(int parent, int depth)
        {
            if (!children.TryGetValue(parent, out var list))
                return;

            for (int i = list.Count - 1; i >= 0; i--)
                pending.Push((list[i], depth));
        }
    }

    private static BranchTotal Sum(TaskSample root, Dictionary<int, List<TaskSample>> children)
    {
        int processes = 0, threads = 0;
        long workingSet = 0;
        double? cpu = null, io = null;
        Stack<TaskSample> pending = [];
        pending.Push(root);

        while (pending.TryPop(out var task))
        {
            processes++;
            threads += task.Info.Threads;
            workingSet += task.Info.WorkingSet;
            cpu = Add(cpu, task.Cpu);
            io = Add(io, task.Io);

            if (children.TryGetValue(task.Info.Key.Pid, out List<TaskSample>? list))
            {
                foreach (TaskSample child in list)
                {
                    pending.Push(child);
                }
            }
        }

        return new BranchTotal(processes, cpu is { } c ? Math.Min(c, 100) : null, workingSet, io, threads);
    }

    private static double? Add(double? a, double? b) => a is null ? b : b is null ? a : a + b;
    public static TaskSample? ValidParent(TaskSample task, IReadOnlyDictionary<int, TaskSample> byPid) => byPid.TryGetValue(task.Info.ParentId, out var parent) && parent.Info.Key.Created < task.Info.Key.Created ? parent : null;
}

using Pulse.Telemetry;

namespace Pulse.Models;

internal enum SortField { Name, Pid, Cpu, Ram, Io, Threads }
internal readonly record struct Branch(TaskSample Task, int Depth, bool HasChildren);

internal static class TaskOrder
{
    public static IEnumerable<Branch> Build(IReadOnlyList<TaskSample> source, string search, SortField sort,
        bool descending, bool tree, HashSet<ProcessKey> collapsed)
    {
        search = search.Trim();
        bool Match(TaskSample t) => t.Info.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            t.Info.Key.Pid.ToString().Contains(search, StringComparison.Ordinal);

        var sorted = source.ToList();
        sorted.Sort((a,b) =>
        {
            int result = sort switch
            {
                SortField.Name => StringComparer.OrdinalIgnoreCase.Compare(a.Info.Name, b.Info.Name),
                SortField.Pid => a.Info.Key.Pid.CompareTo(b.Info.Key.Pid),
                SortField.Cpu => Nullable.Compare(a.Cpu, b.Cpu),
                SortField.Ram => a.Info.WorkingSet.CompareTo(b.Info.WorkingSet),
                SortField.Io => Nullable.Compare(a.Io, b.Io),
                _ => a.Info.Threads.CompareTo(b.Info.Threads)
            };
            if (sort is SortField.Cpu or SortField.Io)
            {
                bool missingA = (sort == SortField.Cpu ? a.Cpu : a.Io) is null;
                bool missingB = (sort == SortField.Cpu ? b.Cpu : b.Io) is null;
                if (missingA != missingB) return missingA ? 1 : -1;
            }
            return result == 0 ? a.Info.Key.Pid.CompareTo(b.Info.Key.Pid) : descending ? -result : result;
        });
        if (!tree) return sorted.Where(Match).Select(t => new Branch(t, 0, false));

        var byPid = source.ToDictionary(t => t.Info.Key.Pid);
        var children = new Dictionary<int, List<TaskSample>>();
        foreach (var t in sorted)
        {
            int parent = ValidParent(t, byPid)?.Info.Key.Pid ?? 0;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(t);
        }

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
            if (search.Length != 0 && !visible.Contains(node.Task.Info.Key)) continue;
            result.Add(new(node.Task, node.Depth, children.ContainsKey(node.Task.Info.Key.Pid)));
            if (search.Length != 0 || !collapsed.Contains(node.Task.Info.Key)) Push(node.Task.Info.Key.Pid, node.Depth + 1);
        }
        return result;
    }

    // A reused parent PID can belong to a process started after the child.
    public static TaskSample? ValidParent(TaskSample task, IReadOnlyDictionary<int, TaskSample> byPid) =>
        byPid.TryGetValue(task.Info.ParentId, out var parent) && parent.Info.Key.Created < task.Info.Key.Created ? parent : null;
}


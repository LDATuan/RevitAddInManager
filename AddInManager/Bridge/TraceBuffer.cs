using System.Diagnostics;

namespace RevitAddinManager.Bridge;

/// <summary>
/// Keeps the latest Debug/Trace output in memory so MCP clients can read it back by sequence number.
/// </summary>
public sealed class TraceBuffer : TraceListener
{
    private const int Capacity = 2000;
    private static readonly object SyncRoot = new object();
    private static readonly LinkedList<TraceEntry> Entries = new LinkedList<TraceEntry>();
    private static long lastSeq;

    public static long LastSeq
    {
        get
        {
            lock (SyncRoot) return lastSeq;
        }
    }

    public static void EnsureRegistered()
    {
        lock (SyncRoot)
        {
            if (Trace.Listeners.OfType<TraceBuffer>().Any()) return;
            Trace.Listeners.Add(new TraceBuffer());
        }
    }

    public static List<TraceEntry> ReadSince(long sinceSeq, int max)
    {
        lock (SyncRoot)
        {
            return Entries.Where(x => x.Seq > sinceSeq).Take(max).ToList();
        }
    }

    public override void Write(string message) => Append(message);

    public override void WriteLine(string message) => Append(message);

    private static void Append(string message)
    {
        if (message == null) return;
        lock (SyncRoot)
        {
            Entries.AddLast(new TraceEntry(++lastSeq, DateTime.Now, message));
            if (Entries.Count > Capacity) Entries.RemoveFirst();
        }
    }
}

public sealed class TraceEntry
{
    public TraceEntry(long seq, DateTime time, string message)
    {
        Seq = seq;
        Time = time;
        Message = message;
    }

    public long Seq { get; }
    public DateTime Time { get; }
    public string Message { get; }
}

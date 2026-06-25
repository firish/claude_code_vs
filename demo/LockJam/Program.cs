using System;
using System.Threading;

namespace LockJam;

internal sealed class Account
{
    public Account(string id) => Id = id;
    public string Id { get; }
    public readonly object Gate = new object();
    public decimal Balance = 1000m;
}

internal static class Program
{
    private static readonly Account A = new Account("A");
    private static readonly Account B = new Account("B");
    private static readonly Account C = new Account("C");


    private static readonly Barrier Lined = new Barrier(3);


    private static readonly SemaphoreSlim Jobs = new SemaphoreSlim(0);

    private static volatile bool _auditing = true;

    private static void Main()
    {
        var threads = new[]
        {
            Worker("xfer A->B", () => Transfer(A, B, 10m)),
            Worker("xfer B->C", () => Transfer(B, C, 20m)),
            Worker("xfer C->A", () => Transfer(C, A, 30m)),  
            Worker("audit",     Audit),                     
            Worker("dispatch",  Dispatch),                   
        };


        foreach (var t in threads) t.Join(); 
        Console.WriteLine("settled"); 
    }


    private static void Transfer(Account from, Account to, decimal amount)
    {
        lock (from.Gate)
        {
            Lined.SignalAndWait();   
            lock (to.Gate)   
            {
                from.Balance -= amount;
                to.Balance += amount;
            }
        }
    }


    private static void Audit()
    {
        long ticks = 0;
        while (_auditing)
            ticks = unchecked(ticks * 1664525 + 1013904223);
        GC.KeepAlive(ticks);
    }

    private static void Dispatch() => Jobs.Wait();

    private static Thread Worker(string name, Action body)
    {
        var t = new Thread(() => body()) { Name = name, IsBackground = true };
        t.Start();
        return t;
    }
}

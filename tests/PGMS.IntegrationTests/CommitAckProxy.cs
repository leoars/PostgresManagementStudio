using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

// Test-only wire proxy. Fixed destination is the isolated cluster; no user servers,
// TLS termination, credentials, payload dumps, or production dependency.
internal sealed class CommitAckProxy : IAsyncDisposable
{
    private readonly TcpListener listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource stop=new();
    private readonly List<Task> clients=[];
    private readonly bool beforeForward;
    private readonly Task accepting;
    private int faulted;
    private int armed=1,refuseNew;
    private int frontendCommits,backendCommits;
    internal CommitAckProxy(bool beforeForward)
    {
        this.beforeForward=beforeForward;listener.Start();
        Port=((IPEndPoint)listener.LocalEndpoint).Port;accepting=AcceptAsync();
    }
    internal int Port { get; }
    internal bool FaultInjected=>Volatile.Read(ref faulted)!=0;
    internal int FrontendCommits=>Volatile.Read(ref frontendCommits);
    internal int BackendCommits=>Volatile.Read(ref backendCommits);
    internal bool RefuseNewConnections { get=>Volatile.Read(ref refuseNew)!=0; set=>Volatile.Write(ref refuseNew,value?1:0); }
    internal bool Armed { get=>Volatile.Read(ref armed)!=0; set=>Volatile.Write(ref armed,value?1:0); }
    private async Task AcceptAsync()
    {
        try { while(!stop.IsCancellationRequested) { var client=await listener.AcceptTcpClientAsync(stop.Token);clients.Add(HandleAsync(client)); } }
        catch(OperationCanceledException)when(stop.IsCancellationRequested){}
    }
    private static async Task<byte[]> FrameAsync(NetworkStream stream,bool startup,CancellationToken token)
    {
        byte[] header=new byte[startup?4:5];
        await stream.ReadExactlyAsync(header,token);
        int length=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(startup?0:1));
        if(length<4 || length>1024*1024)throw new IOException("Invalid isolated proxy frame length.");
        byte[] frame=new byte[length+(startup?0:1)];header.CopyTo(frame,0);
        await stream.ReadExactlyAsync(frame.AsMemory(header.Length),token);return frame;
    }
    private static bool IsCommit(byte[] frame)
    {
        int start=5;
        if(frame[0]==(byte)'P') {while(start<frame.Length && frame[start]!=0)start++;start++;}
        else if(frame[0]!=(byte)'Q')return false;
        int end=start;while(end<frame.Length && frame[end]!=0)end++;
        return Encoding.UTF8.GetString(frame,start,end-start).Trim().TrimEnd(';').Equals("COMMIT",StringComparison.OrdinalIgnoreCase);
    }
    private async Task HandleAsync(TcpClient client)
    {
        if(RefuseNewConnections){client.Dispose();return;}
        using(client)
        using(var server=new TcpClient())
        using(var lifetime=CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            await server.ConnectAsync(IPAddress.Loopback,55439,lifetime.Token);
            var upstream=server.GetStream();var downstream=client.GetStream();
            await upstream.WriteAsync(await FrameAsync(downstream,true,lifetime.Token),lifetime.Token);
            async Task ForwardAsync(bool frontend)
            {
                var source=frontend?downstream:upstream;var destination=frontend?upstream:downstream;
                while(!lifetime.IsCancellationRequested)
                {
                    var frame=await FrameAsync(source,false,lifetime.Token);
                    bool frontCommit=frontend && IsCommit(frame);
                    bool backCommit=!frontend && frame[0]==(byte)'C' && Encoding.UTF8.GetString(frame,5,frame.Length-6)=="COMMIT";
                    if(frontCommit)Interlocked.Increment(ref frontendCommits);
                    if(backCommit)Interlocked.Increment(ref backendCommits);
                    bool trigger=beforeForward?frontCommit:backCommit;
                    if(Armed && trigger && Interlocked.CompareExchange(ref faulted,1,0)==0)return;
                    await destination.WriteAsync(frame,lifetime.Token);
                }
            }
            Task first=ForwardAsync(true),second=ForwardAsync(false);
            await Task.WhenAny(first,second);lifetime.Cancel();client.Close();server.Close();
            try{await Task.WhenAll(first,second);}catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException){}
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();listener.Stop();await accepting;
        try{await Task.WhenAll(clients);}catch(Exception ex)when(ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException){}
        stop.Dispose();
    }
}

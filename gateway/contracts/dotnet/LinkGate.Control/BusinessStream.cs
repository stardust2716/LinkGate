using System.Text;
using System.Buffers.Binary;

namespace LinkGate.Control;

/// <summary>Directional FIN and bounded payload framing. Authentication belongs to the owning transport.</summary>
public sealed class BusinessStream(Stream transport) : Stream
{
    public const int MaxPayload=16384;
    private readonly SemaphoreSlim reading=new(1,1),writing=new(1,1),writeOperation=new(1,1);
    private byte[] buffered=[];
    private int offset;
    private bool eof,acknowledged;
    private volatile bool finished,disposed;
    private long received,sent;
    public long PayloadBytesRead=>Interlocked.Read(ref received);
    public long PayloadBytesWritten=>Interlocked.Read(ref sent);
    public override bool CanRead=>!disposed;
    public override bool CanWrite=>!disposed&&!finished;
    public override bool CanSeek=>false;
    public override long Length=>throw new NotSupportedException();
    public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
    public override async ValueTask<int> ReadAsync(Memory<byte> destination,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(destination.Length==0)return 0;
        if(!await reading.WaitAsync(0,cancellationToken).ConfigureAwait(false))throw new InvalidOperationException("Concurrent business read.");
        try
        {
            if(eof)return 0;
            while(offset==buffered.Length)
            {
                var frame=await ReadFrameAsync(cancellationToken).ConfigureAwait(false)??throw new IOException("Business transport interrupted.");
                if(frame.Length==1&&frame[0]==4){AcceptAcknowledgement();continue;}
                if(frame.Length==1&&frame[0]==2)
                {
                    eof=true;buffered=[];
                    // Only the single reader can enqueue this acknowledgement behind the active writer.
                    await writing.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try{await FrameCodec.WriteAsync(transport,new byte[]{4},cancellationToken).ConfigureAwait(false);}
                    finally{writing.Release();}
                    return 0;
                }
                if(frame.Length is >1 and <=65 && frame[0]==3)throw new BusinessStreamClosedException(Encoding.ASCII.GetString(frame,1,frame.Length-1));
                if(frame.Length is <2 or >MaxPayload+1 || frame[0]!=1)throw new IOException("Invalid business frame.");
                buffered=frame;offset=1;
            }
            int length=Math.Min(destination.Length,buffered.Length-offset);buffered.AsMemory(offset,length).CopyTo(destination);offset+=length;
            Interlocked.Add(ref received,length);return length;
        }
        finally{reading.Release();}
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source,CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if(finished)throw new IOException("Business write direction closed.");
            while(!source.IsEmpty)
            {
                int size=Math.Min(MaxPayload,source.Length);byte[] frame=new byte[size+1];frame[0]=1;source[..size].CopyTo(frame.AsMemory(1));
                await FrameCodec.WriteAsync(transport,frame,cancellationToken).ConfigureAwait(false);Interlocked.Add(ref sent,size);source=source[size..];
            }
        }
        finally{LeaveWrite();}
    }
    public async Task CompleteWritesAsync(CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        await EnterWriteAsync(cancellationToken).ConfigureAwait(false);
        try{if(!finished){finished=true;await FrameCodec.WriteAsync(transport,new byte[]{2},cancellationToken).ConfigureAwait(false);}}
        finally{LeaveWrite();}
    }
    private async Task EnterWriteAsync(CancellationToken token)
    {
        if(!await writeOperation.WaitAsync(0,token).ConfigureAwait(false))throw new InvalidOperationException("Concurrent business write.");
        try{await writing.WaitAsync(token).ConfigureAwait(false);}catch{writeOperation.Release();throw;}
    }
    private void LeaveWrite(){writing.Release();writeOperation.Release();}
    public async Task DrainWriteCompletionAsync(CancellationToken cancellationToken=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(!eof||!finished)throw new InvalidOperationException("Drain requires both directions to be finished.");
        if(!await reading.WaitAsync(0,cancellationToken).ConfigureAwait(false))throw new InvalidOperationException("Concurrent business read.");
        try
        {
            if(acknowledged)return;
            var frame=await ReadFrameAsync(cancellationToken).ConfigureAwait(false)??throw new IOException("Business FIN acknowledgement lost.");
            if(frame.Length!=1||frame[0]!=4)throw new IOException("Invalid business FIN acknowledgement.");AcceptAcknowledgement();
        }
        finally{reading.Release();}
    }
    private void AcceptAcknowledgement()
    {if(!finished||acknowledged)throw new IOException("Unexpected business FIN acknowledgement.");acknowledged=true;}
    private async Task<byte[]?> ReadFrameAsync(CancellationToken token)
    {
        byte[] header=new byte[4];int size=await transport.ReadAtLeastAsync(header,4,false,token).ConfigureAwait(false);
        if(size==0)return null;if(size!=4)throw new IOException("Truncated business frame.");
        uint length=BinaryPrimitives.ReadUInt32LittleEndian(header);
        if(length is <1 or >MaxPayload+1)throw new IOException("Business frame exceeds its limit.");
        byte[] frame=new byte[length];await transport.ReadExactlyAsync(frame,token).ConfigureAwait(false);return frame;
    }
    public async Task SendCloseAsync(string code,CancellationToken cancellationToken=default)
    {
        if(disposed)return;
        if(!await writing.WaitAsync(0,cancellationToken).ConfigureAwait(false))return;
        try{byte[] text=Encoding.ASCII.GetBytes(BusinessStreamClosedException.Normalize(code));byte[] frame=new byte[text.Length+1];frame[0]=3;text.CopyTo(frame,1);await FrameCodec.WriteAsync(transport,frame,cancellationToken).ConfigureAwait(false);finished=true;}
        finally{writing.Release();}
    }
    public override int Read(byte[] buffer,int offset,int count)=>ReadAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer,int offset,int count)=>WriteAsync(buffer.AsMemory(offset,count)).AsTask().GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>ReadAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken token)=>WriteAsync(buffer.AsMemory(offset,count),token).AsTask();
    public override void Flush()=>transport.Flush();
    public override Task FlushAsync(CancellationToken token)=>transport.FlushAsync(token);
    public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
    protected override void Dispose(bool disposing){if(disposed)return;disposed=true;if(disposing)transport.Dispose();base.Dispose(disposing);}
    public override async ValueTask DisposeAsync(){if(disposed)return;disposed=true;await transport.DisposeAsync().ConfigureAwait(false);GC.SuppressFinalize(this);}
}

public sealed class BusinessStreamClosedException : IOException
{
    public string Code{get;}
    public BusinessStreamClosedException(string code):base("Business stream closed."){Code=Normalize(code);}
    internal static string Normalize(string code)=>code is "AUTH_REVOKED" or "LEASE_EXPIRED" or "CONNECTION_CLOSED" or "PROTOCOL_ERROR"?code:"CONNECTION_CLOSED";
}

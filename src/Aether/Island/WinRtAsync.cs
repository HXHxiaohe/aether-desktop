using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Aether.Island;

/// <summary>
/// await support for WinRT async operations. (These helpers normally come with the 24 MB
/// Windows SDK projection, which Aether deliberately doesn't ship.)
/// </summary>
internal static class WinRtAsync
{
    public static Task<T> AsTask<T>(this IAsyncOperation<T> op)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Completed = (o, status) =>
        {
            try
            {
                switch (status)
                {
                    case AsyncStatus.Completed: tcs.TrySetResult(o.GetResults()); break;
                    case AsyncStatus.Canceled: tcs.TrySetCanceled(); break;
                    default: tcs.TrySetException(o.ErrorCode is { } e ? e : new Exception("WinRT operation failed")); break;
                }
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };
        return tcs.Task;
    }

    public static Task<TResult> AsTask<TResult, TProgress>(this IAsyncOperationWithProgress<TResult, TProgress> op)
    {
        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        op.Completed = (o, status) =>
        {
            try
            {
                switch (status)
                {
                    case AsyncStatus.Completed: tcs.TrySetResult(o.GetResults()); break;
                    case AsyncStatus.Canceled: tcs.TrySetCanceled(); break;
                    default: tcs.TrySetException(o.ErrorCode is { } e ? e : new Exception("WinRT operation failed")); break;
                }
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };
        return tcs.Task;
    }

    public static TaskAwaiter<T> GetAwaiter<T>(this IAsyncOperation<T> op) => op.AsTask().GetAwaiter();

    public static TaskAwaiter<TResult> GetAwaiter<TResult, TProgress>(this IAsyncOperationWithProgress<TResult, TProgress> op) => op.AsTask().GetAwaiter();

    // ---- IBuffer -> bytes via IBufferByteAccess (no DataReader projection needed) ----
    [ComImport, Guid("905a0fef-bc53-11df-8c49-001e4fc686da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBufferByteAccess
    {
        IntPtr Buffer();
    }

    public static byte[] ToArray(this IBuffer buffer)
    {
        var len = (int)buffer.Length;
        var bytes = new byte[len];
        if (len == 0) return bytes;
        var ptr = ((WinRT.IWinRTObject)buffer).NativeObject.ThisPtr;
        var iid = typeof(IBufferByteAccess).GUID;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(ptr, in iid, out var accessPtr));
        try
        {
            var access = (IBufferByteAccess)Marshal.GetObjectForIUnknown(accessPtr);
            Marshal.Copy(access.Buffer(), bytes, 0, len);
        }
        finally { Marshal.Release(accessPtr); }
        return bytes;
    }

    /// <summary>Read a whole WinRT stream (album art) into memory, capped at 8 MB.</summary>
    public static async Task<byte[]> ReadAllAsync(this IRandomAccessStreamWithContentType stream)
    {
        var size = (uint)Math.Min(stream.Size, 8u * 1024 * 1024);
        if (size == 0) return Array.Empty<byte>();
        var buffer = new Windows.Storage.Streams.Buffer(size);
        var result = await stream.ReadAsync(buffer, size, InputStreamOptions.None);
        return result.ToArray();
    }
}

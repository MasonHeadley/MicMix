using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MicMix.Audio;

/// <summary>
/// Captures the audio output of a single process (and its child processes)
/// using the Windows process loopback API. Needs Windows 10 build 20348+ or Windows 11.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private const string VirtualLoopbackDevice = "VAD\\Process_Loopback";
    private const ushort VT_BLOB = 65;

    private readonly int _processId;
    private AudioClient? _client;
    private Thread? _thread;
    private volatile bool _stopping;

    public WaveFormat WaveFormat { get; }
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<Exception>? Failed;

    public ProcessLoopbackCapture(int processId, int sampleRate)
    {
        _processId = processId;
        WaveFormat = new WaveFormat(sampleRate, 16, 2);
    }

    public void Start()
    {
        // Activation has to happen off the UI (STA) thread or the callback never arrives.
        var iface = Task.Run(() => ActivateAsync(_processId)).GetAwaiter().GetResult();
        _client = new AudioClient(iface);
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            200_000, // 20 ms in 100ns units
            0,
            WaveFormat,
            Guid.Empty);

        _stopping = false;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "App capture", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _thread?.Join(1000);
        _thread = null;
    }

    private void CaptureLoop()
    {
        var client = _client!;
        using var ready = new AutoResetEvent(false);
        try
        {
            client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
            var capture = client.AudioCaptureClient;
            int blockAlign = WaveFormat.BlockAlign;
            var buffer = new byte[WaveFormat.AverageBytesPerSecond / 10];
            client.Start();

            while (!_stopping)
            {
                if (!ready.WaitOne(100)) continue;

                int packet = capture.GetNextPacketSize();
                while (packet > 0 && !_stopping)
                {
                    var ptr = capture.GetBuffer(out int frames, out var flags);
                    int bytes = frames * blockAlign;
                    if (buffer.Length < bytes) buffer = new byte[bytes];

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                        Array.Clear(buffer, 0, bytes);
                    else
                        Marshal.Copy(ptr, buffer, 0, bytes);

                    capture.ReleaseBuffer(frames);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytes));
                    packet = capture.GetNextPacketSize();
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(this, ex);
        }
    }

    private static async Task<IAudioClient> ActivateAsync(int processId)
    {
        var activationParams = new AudioClientActivationParamsNative
        {
            ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0, // PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE
        };

        int paramsSize = Marshal.SizeOf<AudioClientActivationParamsNative>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(paramsSize);
        IntPtr propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var prop = new PropVariantBlob { vt = VT_BLOB, cbSize = (uint)paramsSize, pBlobData = paramsPtr };
            Marshal.StructureToPtr(prop, propPtr, false);

            var handler = new ActivationHandler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync(VirtualLoopbackDevice, iid, propPtr, handler, out _);
            return await handler.Result.ConfigureAwait(false);
        }
        finally
        {
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    public void Dispose()
    {
        Stop();
        _client?.Dispose();
        _client = null;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParamsNative
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Result => _tcs.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out int hr, out object iface);
                if (hr < 0)
                    _tcs.TrySetException(Marshal.GetExceptionForHR(hr) ?? new COMException("Activation failed", hr));
                else
                    _tcs.TrySetResult((IAudioClient)iface);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        }
    }
}

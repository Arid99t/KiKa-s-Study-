using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using UnityEngine;
using NaughtyAttributes;

/// <summary>Payload fired when the Teensy hardware sensor registers a tap.</summary>
public readonly struct HardwareTapEvent
{
    public readonly float gameTime;
    public readonly int tapCount;
    public readonly int force;
    public HardwareTapEvent(float gameTime, int tapCount, int force)
    { this.gameTime = gameTime; this.tapCount = tapCount; this.force = force; }
}

public class ARMETapDetection : MonoBehaviour
{
    [BoxGroup("Serial Settings")]
    [Tooltip("COM port for the Teensy")]
    [Dropdown("GetAvailablePorts")]
    [SerializeField] private string portName = "";

    [BoxGroup("Serial Settings")]
    [Tooltip("Baud rate matching Teensy Serial.begin()")]
    [SerializeField] private int baudRate = 115200;

    [BoxGroup("Serial Settings")]
    [SerializeField] private bool autoConnectOnStart = true;

    [HorizontalLine(color: EColor.White)]

    [BoxGroup("Connection")]
    [ReadOnly, AllowNesting]
    [SerializeField] private bool isConnected;

    [BoxGroup("Tap Data")]
    [ReadOnly, AllowNesting]
    [SerializeField] private bool tapDetected;

    [BoxGroup("Tap Data")]
    [ReadOnly, AllowNesting]
    [SerializeField] private int tapCount;

    [BoxGroup("Tap Data")]
    [ReadOnly, AllowNesting]
    [SerializeField] private int lastForce;

    [BoxGroup("Tap Data")]
    [ProgressBar("Force", 4095, EColor.Red)]
    [SerializeField] private int forceBar;

    [BoxGroup("Tap Data")]
    [ReadOnly, AllowNesting]
    [SerializeField] private float timeSinceLastTap;

    [HorizontalLine(color: EColor.White)]

    [BoxGroup("Tempo")]
    [Tooltip("Reference BPM that maps to 1.0x playback speed")]
    [SerializeField] private float referenceBPM = 120f;

    [BoxGroup("Tempo")]
    [Tooltip("Ignore taps that arrive closer together than this (seconds). Filters contact bounce / streamed sensor lines so one physical press counts once.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float minTapInterval = 0.12f;

    [BoxGroup("Tempo")]
    [Tooltip("How many recent taps to average for tempo calculation")]
    [Range(2, 16)]
    [SerializeField] private int tempoWindowSize = 4;

    [BoxGroup("Tempo")]
    [Tooltip("How quickly playback speed adjusts (lower = smoother)")]
    [Range(0.5f, 20f)]
    [SerializeField] private float speedSmoothRate = 5f;

    [BoxGroup("Tempo")]
    [Tooltip("Minimum playback speed")]
    [SerializeField] private float minSpeed = 0.25f;

    [BoxGroup("Tempo")]
    [Tooltip("Maximum playback speed")]
    [SerializeField] private float maxSpeed = 3f;

    [BoxGroup("Tempo")]
    [ReadOnly, AllowNesting]
    [SerializeField] private float currentBPM;

    [BoxGroup("Tempo")]
    [ReadOnly, AllowNesting]
    [SerializeField] private float playbackSpeedRatio = 1f;

    [BoxGroup("Tap Log")]
    [ReadOnly, AllowNesting]
    [ResizableTextArea]
    [SerializeField] private string tapLog = "";

    /// <summary>Current playback speed ratio (1.0 = reference tempo). Use this to drive VideoPlayer.playbackSpeed.</summary>
    public float PlaybackSpeedRatio => playbackSpeedRatio;

    /// <summary>Current estimated BPM from tap tempo.</summary>
    public float CurrentBPM => currentBPM;

    private SerialPort _serialPort;             // Windows serial path
    private System.IO.Stream _readStream;       // Windows: SerialPort.BaseStream consumed by the read thread
    private int _fd = -1;                        // macOS/Linux: raw non-blocking file descriptor
    private readonly object _connectLock = new object();
    private float _lastTapTimestamp;
    private float _lastAcceptedTapTime = -999f;
    private const int MaxLogLines = 12;
    private float _tapFlashTimer;

    private Thread _readThread;
    private volatile bool _keepReading;
    private volatile bool _loggedFirstData;
    private readonly ConcurrentQueue<string> _lineQueue = new ConcurrentQueue<string>();

    // ── Data Logging Event ───────────────────────────────────────────────
    public event System.Action<HardwareTapEvent> OnHardwareTap;

    private readonly System.Collections.Generic.List<float> _tapTimestamps = new System.Collections.Generic.List<float>();
    private float _targetSpeed = 1f;

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
    // Native POSIX I/O so the device can be opened NON-BLOCKING and polled. A blocking managed read
    // holds a SafeHandle ref that makes Disconnect's stream-close wait forever (the hang). With a raw
    // fd + O_NONBLOCK the read thread never parks, so Disconnect tears down instantly.
    private const int O_RDONLY = 0x0000;
#if UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
    private const int O_NONBLOCK = 0x800;     // Linux
#else
    private const int O_NONBLOCK = 0x0004;    // macOS / Darwin
#endif

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern int open(string path, int oflag);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern System.IntPtr read(int fd, byte[] buf, System.IntPtr count);
#endif

    private DropdownList<string> GetAvailablePorts()
    {
        var list = new DropdownList<string>();
        var seen = new System.Collections.Generic.HashSet<string>();

        void Offer(string p)
        {
            if (!string.IsNullOrEmpty(p) && seen.Add(p))
                list.Add(p, p);
        }

#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
        // Mono's SerialPort.GetPortNames() is unreliable on macOS — it typically returns only the
        // Bluetooth port and misses USB devices. Enumerate /dev directly and prefer the cu.* ports.
        try
        {
            // GetFileSystemEntries (unlike GetFiles) returns device nodes, which Mono otherwise
            // filters out — that's why the dropdown came up empty.
            foreach (string dev in System.IO.Directory.GetFileSystemEntries("/dev", "cu.*"))
                Offer(dev);
            foreach (string dev in System.IO.Directory.GetFileSystemEntries("/dev", "tty.usb*"))
                Offer(dev);
        }
        catch { /* /dev not enumerable on this platform — fall back to GetPortNames below. */ }
#endif

        foreach (string port in SerialPort.GetPortNames())
        {
            // On macOS, GetPortNames() returns the /dev/tty.* devices, but those block on open
            // under Mono — the call-unit (/dev/cu.*) device is the one that actually reads. Offer
            // the cu.* variant first so it's the natural pick.
            if (port.StartsWith("/dev/tty."))
                Offer("/dev/cu." + port.Substring("/dev/tty.".Length));
            Offer(port);
        }

        if (seen.Count == 0)
            list.Add("No ports found", "");

        return list;
    }

    /// <summary>
    /// On macOS the /dev/tty.* serial device blocks on open under Mono; the equivalent /dev/cu.*
    /// device is the one that delivers data. Translate a chosen tty.* port to cu.* so a port picked
    /// from the dropdown still works.
    /// </summary>
    private static string ResolvePortName(string port)
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
        if (!string.IsNullOrEmpty(port) && port.StartsWith("/dev/tty."))
            return "/dev/cu." + port.Substring("/dev/tty.".Length);
#endif
        return port;
    }

    void Start()
    {
        if (autoConnectOnStart)
            Connect();
    }

    void Update()
    {
        if (!isConnected)
            return;

        timeSinceLastTap = Time.time - _lastTapTimestamp;

        // Flash tap indicator off after 0.15s
        if (tapDetected && Time.time - _tapFlashTimer > 0.15f)
            tapDetected = false;

        // Process lines received from background thread
        while (_lineQueue.TryDequeue(out string line))
        {
            ParseTapLine(line);
        }

        // Smoothly interpolate playback speed toward target
        playbackSpeedRatio = Mathf.Lerp(playbackSpeedRatio, _targetSpeed, Time.deltaTime * speedSmoothRate);
    }

    private void ReadSerialThread()
    {
        var bytes = new byte[1024];
        string buffer = "";

        while (_keepReading)
        {
            int n;
            try
            {
                n = ReadChunk(bytes);
            }
            catch (System.Exception ex)
            {
                // Source closed/disposed (Disconnect) — exit. Log only if it wasn't a deliberate stop.
                if (_keepReading)
                    Debug.LogWarning($"[TapDetection] read loop stopped: {ex.Message}");
                break;
            }

            if (n <= 0)
            {
                // No data available right now (non-blocking read / timeout). Brief sleep, then
                // re-check _keepReading so Disconnect can stop us promptly without blocking.
                Thread.Sleep(3);
                continue;
            }

            if (!_loggedFirstData)
            {
                _loggedFirstData = true;
                Debug.Log("[TapDetection] Receiving serial data from the sensor.");
            }

            buffer += System.Text.Encoding.ASCII.GetString(bytes, 0, n);

            int newlineIndex;
            while ((newlineIndex = buffer.IndexOf('\n')) >= 0)
            {
                string line = buffer.Substring(0, newlineIndex).Trim();
                buffer = buffer.Substring(newlineIndex + 1);

                if (line.Length > 0)
                    _lineQueue.Enqueue(line);
            }
        }
    }

    /// <summary>
    /// Reads a chunk of bytes from the active source. Returns the number of bytes read, or &lt;= 0 when
    /// nothing is available right now (so the caller sleeps briefly and re-checks the stop flag).
    /// macOS/Linux use a non-blocking raw fd; Windows uses the SerialPort stream with a read timeout.
    /// </summary>
    private int ReadChunk(byte[] buf)
    {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
        int fd = _fd;
        if (fd < 0) return -1;
        // Non-blocking: returns -1 with EAGAIN when there's no data, which we treat as "nothing yet".
        return read(fd, buf, (System.IntPtr)buf.Length).ToInt32();
#else
        var stream = _readStream;
        if (stream == null) return -1;
        try { return stream.Read(buf, 0, buf.Length); }
        catch (System.TimeoutException) { return 0; }   // SerialPort read timeout — nothing this round.
#endif
    }

    private void ParseTapLine(string line)
    {
        // Expected format: "TAP #1 | Force: 230"
        if (!line.StartsWith("TAP")) return;

        // Parse tap count
        int hashIndex = line.IndexOf('#');
        int pipeIndex = line.IndexOf('|');
        if (hashIndex < 0 || pipeIndex < 0) return;

        string countStr = line.Substring(hashIndex + 1, pipeIndex - hashIndex - 1).Trim();
        if (int.TryParse(countStr, out int count))
            tapCount = count;

        // Parse force value
        int colonIndex = line.LastIndexOf(':');
        if (colonIndex < 0) return;

        string forceStr = line.Substring(colonIndex + 1).Trim();
        if (int.TryParse(forceStr, out int force))
        {
            lastForce = force;
            forceBar = force;
        }

        // Debounce: drop bounce / streamed lines that arrive too soon after the last accepted tap,
        // so one physical press registers once. (Force bar above still updates for live feedback.)
        if (Time.time - _lastAcceptedTapTime < minTapInterval)
            return;
        _lastAcceptedTapTime = Time.time;

        _lastTapTimestamp = Time.time;
        timeSinceLastTap = 0f;
        tapDetected = true;
        _tapFlashTimer = Time.time;

        OnHardwareTap?.Invoke(new HardwareTapEvent(Time.time, tapCount, lastForce));

        // Tempo calculation
        _tapTimestamps.Add(Time.time);
        if (_tapTimestamps.Count > tempoWindowSize)
            _tapTimestamps.RemoveAt(0);

        if (_tapTimestamps.Count >= 2)
        {
            float totalInterval = _tapTimestamps[_tapTimestamps.Count - 1] - _tapTimestamps[0];
            float avgInterval = totalInterval / (_tapTimestamps.Count - 1);
            currentBPM = 60f / avgInterval;
            _targetSpeed = Mathf.Clamp(currentBPM / referenceBPM, minSpeed, maxSpeed);
        }

        AppendLog($"[{Time.time:F2}s] Tap #{tapCount}  Force: {lastForce}  BPM: {currentBPM:F0}");
    }

    private void AppendLog(string entry)
    {
        string[] lines = tapLog.Split('\n');
        if (lines.Length >= MaxLogLines)
        {
            // Keep only the most recent lines
            int start = lines.Length - MaxLogLines + 1;
            tapLog = string.Join("\n", lines, start, lines.Length - start);
        }

        tapLog = string.IsNullOrEmpty(tapLog) ? entry : tapLog + "\n" + entry;
    }

    [Button("Auto-Detect Teensy")]
    private void AutoDetectTeensy()
    {
        string found = null;
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
        try
        {
            var candidates = new System.Collections.Generic.List<string>();
            candidates.AddRange(System.IO.Directory.GetFileSystemEntries("/dev", "cu.usbmodem*"));
            candidates.AddRange(System.IO.Directory.GetFileSystemEntries("/dev", "cu.usbserial*"));
            candidates.AddRange(System.IO.Directory.GetFileSystemEntries("/dev", "tty.usbmodem*"));
            if (candidates.Count > 0) found = candidates[0];
        }
        catch (System.Exception ex) { Debug.LogWarning($"Auto-detect failed: {ex.Message}"); }
#else
        foreach (string p in SerialPort.GetPortNames())
            if (p.IndexOf("usb", System.StringComparison.OrdinalIgnoreCase) >= 0) { found = p; break; }
#endif

        if (!string.IsNullOrEmpty(found))
        {
            portName = ResolvePortName(found);
            Debug.Log($"Teensy port set to {portName}.");
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
        else
        {
            Debug.LogWarning("No Teensy (usbmodem/usbserial) port found. Is it plugged in and set to USB Type 'Serial'?");
        }
    }

    [Button("List Available Ports")]
    private void ListAvailablePorts()
    {
        string[] ports = SerialPort.GetPortNames();
        if (ports.Length == 0)
        {
            Debug.LogWarning("No COM ports found. Is the Teensy plugged in?");
            return;
        }

        Debug.Log($"Available COM ports ({ports.Length}):");
        foreach (string port in ports)
            Debug.Log($"  - {port}");
    }

    [Button("Connect")]
    private void Connect()
    {
        lock (_connectLock)
        {
            if (isConnected)
            {
                Debug.LogWarning("Already connected.");
                return;
            }

            string resolvedPort = ResolvePortName(portName);
            if (string.IsNullOrEmpty(resolvedPort))
            {
                Debug.LogError("No port selected. Click 'Auto-Detect Teensy' or pick a port first.");
                return;
            }

            try
            {
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
                // macOS/Linux: Mono's SerialPort is unreliable here (opens but never delivers bytes),
                // so open the device node directly and NON-BLOCKING — exactly what `cat` does, but
                // pollable so Disconnect can stop the read thread instantly without deadlocking.
                // USB-CDC ignores the baud rate, so no termios configuration is needed for the Teensy.
                int fd = open(resolvedPort, O_RDONLY | O_NONBLOCK);
                if (fd < 0)
                    throw new System.Exception($"open() failed (errno {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                _fd = fd;
#else
                // Windows: SerialPort works well. ReadTimeout keeps the read loop responsive to stop.
                _serialPort = new SerialPort(resolvedPort, baudRate)
                {
                    DtrEnable = true,
                    ReadTimeout = 100
                };
                _serialPort.Open();
                _readStream = _serialPort.BaseStream;
#endif
                _keepReading = true;
                _loggedFirstData = false;
                _readThread = new Thread(ReadSerialThread)
                {
                    IsBackground = true
                };
                _readThread.Start();

                isConnected = true;
                Debug.Log($"Connected to {resolvedPort}.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"Failed to open {resolvedPort}: {ex.Message}");
                Cleanup();
            }
        }
    }

    [Button("Disconnect")]
    private void Disconnect()
    {
        lock (_connectLock)
        {
            if (!isConnected && _readThread == null && _fd < 0 && _serialPort == null)
                return;   // nothing to do

            Cleanup();
            Debug.Log("Disconnected from serial port.");
        }
    }

    /// <summary>
    /// Tears down the read thread and the device safely. The read thread uses non-blocking/timed
    /// reads, so it observes _keepReading == false and exits within a few ms — we Join it BEFORE
    /// closing the descriptor, so the main thread never blocks closing a handle that's in use.
    /// </summary>
    private void Cleanup()
    {
        _keepReading = false;

        var thread = _readThread;
        _readThread = null;
        if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
        {
            if (!thread.Join(1000))
                Debug.LogWarning("[TapDetection] read thread did not stop in time; abandoning it.");
        }

        // Worker has stopped using the source now — safe to close on this thread.
#if UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_EDITOR_LINUX || UNITY_STANDALONE_LINUX
        if (_fd >= 0)
        {
            try { close(_fd); } catch { }
            _fd = -1;
        }
#endif
        _readStream = null;
        if (_serialPort != null)
        {
            try { _serialPort.Dispose(); } catch { }
            _serialPort = null;
        }

        isConnected = false;
    }

    [Button("Clear Log")]
    private void ClearLog()
    {
        tapLog = "";
        tapCount = 0;
        lastForce = 0;
        forceBar = 0;
    }

    void OnDestroy()
    {
        Disconnect();
    }
}

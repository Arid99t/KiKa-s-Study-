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

    [BoxGroup("Tap Log")]
    [ReadOnly, AllowNesting]
    [ResizableTextArea]
    [SerializeField] private string tapLog = "";

    private SerialPort _serialPort;
    private float _lastTapTimestamp;
    private const int MaxLogLines = 12;
    private float _tapFlashTimer;

    private Thread _readThread;
    private volatile bool _keepReading;
    private readonly ConcurrentQueue<string> _lineQueue = new ConcurrentQueue<string>();

    // ── Data Logging Event ───────────────────────────────────────────────
    public event System.Action<HardwareTapEvent> OnHardwareTap;

    private DropdownList<string> GetAvailablePorts()
    {
        var list = new DropdownList<string>();
        string[] ports = SerialPort.GetPortNames();

        if (ports.Length == 0)
        {
            list.Add("No ports found", "");
        }
        else
        {
            foreach (string port in ports)
                list.Add(port, port);
        }

        return list;
    }

    void Start()
    {
        if (autoConnectOnStart)
            Connect();
    }

    void Update()
    {
        if (!isConnected || _serialPort == null || !_serialPort.IsOpen)
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
    }

    private void ReadSerialThread()
    {
        string buffer = "";
        while (_keepReading)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    break;

                string data = _serialPort.ReadExisting();
                if (string.IsNullOrEmpty(data))
                {
                    Thread.Sleep(5);
                    continue;
                }

                buffer += data;

                int newlineIndex;
                while ((newlineIndex = buffer.IndexOf('\n')) >= 0)
                {
                    string line = buffer.Substring(0, newlineIndex).Trim();
                    buffer = buffer.Substring(newlineIndex + 1);

                    if (line.Length > 0)
                        _lineQueue.Enqueue(line);
                }
            }
            catch (System.Exception)
            {
                break;
            }
        }
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

        _lastTapTimestamp = Time.time;
        timeSinceLastTap = 0f;
        tapDetected = true;
        _tapFlashTimer = Time.time;

        OnHardwareTap?.Invoke(new HardwareTapEvent(Time.time, tapCount, lastForce));
        AppendLog($"[{Time.time:F2}s] Tap #{tapCount}  Force: {lastForce}");
        Debug.Log($"Tap #{tapCount} | Force: {lastForce}");
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
        if (isConnected)
        {
            Debug.LogWarning("Already connected.");
            return;
        }

        try
        {
            _serialPort = new SerialPort(portName, baudRate)
            {
                DtrEnable = true,
                ReadTimeout = 100
            };
            _serialPort.Open();
            isConnected = true;

            _keepReading = true;
            _readThread = new Thread(ReadSerialThread)
            {
                IsBackground = true
            };
            _readThread.Start();

            Debug.Log($"Connected to {portName} at {baudRate} baud.");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Failed to open {portName}: {ex.Message}");
            isConnected = false;
        }
    }

    [Button("Disconnect")]
    private void Disconnect()
    {
        _keepReading = false;
        if (_readThread != null && _readThread.IsAlive)
            _readThread.Join(500);
        _readThread = null;

        if (_serialPort != null && _serialPort.IsOpen)
        {
            _serialPort.Close();
            _serialPort.Dispose();
            _serialPort = null;
        }

        isConnected = false;
        Debug.Log("Disconnected from serial port.");
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

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WinScope.UI;

namespace WinScope.Modules.Network;

/// <summary>One app in the apps table. Kept across updates (keyed by app name) so selection and live sorting stay put.</summary>
public sealed class AppRowVm : INotifyPropertyChanged
{
    public AppRowVm(string key) => Key = key;
    public string Key { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private string _name = "", _nameText = "", _tip = "", _downText = "", _upText = "", _totalText = "", _totalTip = "";
    private double _down, _up, _totalRate;
    private long _total, _connSort;
    private int _established, _listening, _udp;

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string NameText { get => _nameText; private set => Set(ref _nameText, value); }
    public string Tip { get => _tip; private set => Set(ref _tip, value); }
    public double Down { get => _down; private set => Set(ref _down, value); }
    public double Up { get => _up; private set => Set(ref _up, value); }
    public double TotalRate { get => _totalRate; private set => Set(ref _totalRate, value); }
    public string DownText { get => _downText; private set => Set(ref _downText, value); }
    public string UpText { get => _upText; private set => Set(ref _upText, value); }
    public long Total { get => _total; private set => Set(ref _total, value); }
    public string TotalText { get => _totalText; private set => Set(ref _totalText, value); }
    public string TotalTip { get => _totalTip; private set => Set(ref _totalTip, value); }
    public int Established { get => _established; private set => Set(ref _established, value); }
    public int ListeningPorts { get => _listening; private set => Set(ref _listening, value); }
    public int Udp { get => _udp; private set => Set(ref _udp, value); }
    /// <summary>Default non-admin order: established connections, then all TCP, then UDP.</summary>
    public long ConnSort { get => _connSort; private set => Set(ref _connSort, value); }

    public void Update(AppStat a)
    {
        Name = a.Name;
        NameText = a.ProcCount > 1 ? $"{a.Name}  ×{a.ProcCount}" : a.Name;
        string procs = a.ProcCount == 0 ? "Nothing open right now" : a.ProcCount == 1 ? $"Process id {a.PidList}" : $"{a.ProcCount} processes (ids {a.PidList})";
        Tip = (a.Path != null ? a.Path + "\n" : "") + procs;
        Down = a.Down; Up = a.Up;
        TotalRate = a.Down + a.Up;
        DownText = a.Down >= 1 ? Format.Rate(a.Down) : "—";
        UpText = a.Up >= 1 ? Format.Rate(a.Up) : "—";
        Total = a.TotalDown + a.TotalUp;
        TotalText = Total > 0 ? Format.Bytes(Total) : "—";
        TotalTip = $"Since WinScope started:  ↓ {Format.Bytes(a.TotalDown)} downloaded,  ↑ {Format.Bytes(a.TotalUp)} uploaded";
        Established = a.Established;
        ListeningPorts = a.ListeningPorts;
        Udp = a.Udp;
        ConnSort = a.Established * 1_000_000L + Math.Min(a.TcpAll, 999) * 1000L + Math.Min(a.Udp, 999);
    }
}

/// <summary>One connection/endpoint row. Immutable: a change of state shows up as a new row.</summary>
public sealed class ConnRowVm
{
    private static readonly Brush InternetBrush = Palette.B(Palette.Accent2);
    private static readonly Brush LanBrush = Palette.B(Palette.Good);
    private static readonly Brush LocalBrush = Palette.B(Palette.Muted);
    private static readonly Brush AnyBrush = Palette.B(Palette.Weak);
    private static readonly Brush SharedBrush = Palette.B(Palette.Fair);

    public ConnRowVm(ConnInfo c, string app)
    {
        Key = c.Key + "|" + app;
        App = app;
        Proto = c.Proto == Network.Proto.Tcp ? "TCP" : "UDP";
        LocalPort = c.LocalPort;
        Remote = c.RemoteText;
        RemoteSort = c.HasRemote ? c.RemoteText : "~";   // endpoints without a remote end sort last
        State = AddressClass.StateText(c);
        StateOrder = AddressClass.StateOrder(c);
        bool bound = !c.HasRemote;
        WhereText = AddressClass.Label(c.Where);
        WhereTip = AddressClass.Explain(c.Where, bound);
        WhereBrush = c.Where switch { Where.Internet => InternetBrush, Where.Lan => LanBrush, Where.AnyNetwork => AnyBrush, Where.Shared => SharedBrush, _ => LocalBrush };
        string what = c.IsListening ? "Listening for incoming connections" : c.Proto == Network.Proto.Udp ? "UDP endpoint (UDP has no fixed remote end)" : "Connection";
        Tip = $"{what}\nLocal: {c.LocalText}" + (c.HasRemote ? $"\nRemote: {c.RemoteText}" : "") + $"\nProcess id {c.Pid}";
    }

    public string Key { get; }
    public string App { get; }
    public string Proto { get; }
    public int LocalPort { get; }
    public string Remote { get; }
    public string RemoteSort { get; }
    public string State { get; }
    public int StateOrder { get; }
    public string WhereText { get; }
    public string WhereTip { get; }
    public Brush WhereBrush { get; }
    public string Tip { get; }
}

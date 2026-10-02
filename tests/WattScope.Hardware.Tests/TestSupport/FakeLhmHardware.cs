using LibreHardwareMonitor.Hardware;

namespace WattScope.Hardware.Tests.TestSupport;

/// <summary>A LibreHardwareMonitor hardware node that only counts how often it was refreshed.</summary>
public sealed class FakeLhmHardware : IHardware
{
    public FakeLhmHardware(string id, HardwareType type = HardwareType.Cpu)
    {
        Identifier = new Identifier(id.Trim('/').Split('/'));
        HardwareType = type;
        Name = id;
    }

    public int UpdateCount { get; private set; }
    public bool ThrowOnUpdate { get; set; }
    public List<FakeLhmSensor> SensorList { get; } = [];
    public List<FakeLhmHardware> Children { get; } = [];

    public FakeLhmSensor AddSensor(string name, float? value, SensorType type = SensorType.Power)
    {
        var sensor = new FakeLhmSensor(this, name, SensorList.Count, type) { Value = value };
        SensorList.Add(sensor);
        return sensor;
    }

    public FakeLhmHardware AddChild(FakeLhmHardware child)
    {
        child.Parent = this;
        Children.Add(child);
        return child;
    }

    public HardwareType HardwareType { get; }
    public Identifier Identifier { get; }
    public string Name { get; set; }
    public IHardware Parent { get; private set; } = null!;
    public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
    public ISensor[] Sensors => [.. SensorList];
    public IHardware[] SubHardware => [.. Children];

    public void Update()
    {
        UpdateCount++;
        if (ThrowOnUpdate)
            throw new InvalidOperationException("simulated update failure");
    }

    public string GetReport() => string.Empty;
    public void Accept(IVisitor visitor) => visitor.VisitHardware(this);
    public void Traverse(IVisitor visitor) { }

#pragma warning disable CS0067 // required by the interface, never raised
    public event SensorEventHandler? SensorAdded;
    public event SensorEventHandler? SensorRemoved;
#pragma warning restore CS0067
}

public sealed class FakeLhmSensor(IHardware hardware, string name, int index, SensorType type) : ISensor
{
    public IControl Control => null!;
    public IHardware Hardware { get; } = hardware;
    public Identifier Identifier { get; } = new(hardware.Identifier, type.ToString().ToLowerInvariant(), index.ToString());
    public int Index { get; } = index;
    public bool IsDefaultHidden => false;
    public float? Max => Value;
    public float? Min => Value;
    public string Name { get; set; } = name;
    public IReadOnlyList<IParameter> Parameters => [];
    public SensorType SensorType { get; } = type;
    public float? Value { get; set; }
    public IEnumerable<SensorValue> Values => [];
    public TimeSpan ValuesTimeWindow { get; set; }

    public void ResetMin() { }
    public void ResetMax() { }
    public void ClearValues() { }
    public void Accept(IVisitor visitor) => visitor.VisitSensor(this);
    public void Traverse(IVisitor visitor) { }
}

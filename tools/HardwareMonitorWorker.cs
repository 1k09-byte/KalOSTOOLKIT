
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

var readings = new List<WorkerReading>();
try
{
    var computer = new Computer
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true
    };
    computer.Open();
    var pending = new Stack<IHardware>(computer.Hardware.Reverse());
    while (pending.Count > 0)
    {
        var hardware = pending.Pop();
        try
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.Value is not float value || float.IsNaN(value) || float.IsInfinity(value)) continue;
                string category = hardware.HardwareType switch
                {
                    HardwareType.Cpu => "CPU",
                    HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia => "GPU",
                    HardwareType.Memory => "Memory",
                    HardwareType.Storage => "Storage",
                    HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => "Motherboard",
                    _ => "Hardware"
                };
                if (category is not ("CPU" or "GPU" or "Memory" or "Storage" or "Motherboard")) continue;
                readings.Add(new WorkerReading(category, hardware.Name, sensor.Name, value, sensor.SensorType.ToString()));
            }
            foreach (var child in hardware.SubHardware.Reverse()) pending.Push(child);
        }
        catch { }
    }
    computer.Close();
}
catch { }

Console.WriteLine(JsonSerializer.Serialize(readings));

record WorkerReading(string Category, string Hardware, string Sensor, double NumericValue, string SensorType);

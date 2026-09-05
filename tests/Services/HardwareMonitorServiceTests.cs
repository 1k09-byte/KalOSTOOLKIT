using System.Collections.Generic;
using System.Linq;
using KaliteKit.Services;

namespace KaliteKit.Tests.Services;

public class HardwareMonitorServiceTests
{
    [Fact]
    public void SelectEssentialReadings_IncludesGpuSmallDataForVramFallback()
    {
        var readings = new List<SystemSensorReading>
        {
            new("GPU", "GPU 0", "GPU Memory Used", "2048 MB", "SmallData") { NumericValue = 2048 },
            new("GPU", "GPU 0", "GPU Memory Total", "8192 MB", "SmallData") { NumericValue = 8192 }
        };

        var selected = HardwareMonitorService.SelectEssentialReadings(readings);

        Assert.Contains(selected, r => r.Sensor == "GPU Memory Used" && r.SensorType == "SmallData");
    }

    [Fact]
    public void SelectEssentialReadings_KeepsOverviewMetricsAndDropsRawSensorNoise()
    {
        var readings = new List<SystemSensorReading>
        {
            new("CPU", "CPU Package", "CPU Total", "42.00 %", "Load") { NumericValue = 42 },
            new("CPU", "CPU Package", "Core 0", "40.00 %", "Load") { NumericValue = 40 },
            new("CPU", "CPU Package", "Core 1", "44.00 %", "Load") { NumericValue = 44 },
            new("CPU", "CPU Package", "CPU Package", "61.00 C", "Temperature") { NumericValue = 61 },
            new("CPU", "CPU Package", "Core 0", "59.00 C", "Temperature") { NumericValue = 59 },
            new("GPU", "GPU 0", "GPU Core", "37.00 %", "Load") { NumericValue = 37 },
            new("GPU", "GPU 0", "GPU Hot Spot", "70.00 C", "Temperature") { NumericValue = 70 },
            new("GPU", "GPU 0", "GPU Memory Used", "2.00 GB", "Data") { NumericValue = 2 },
            new("GPU", "GPU 0", "GPU Memory Total", "8.00 GB", "Data") { NumericValue = 8 },
            new("Memory", "System memory", "Memory Used", "8.00 GB", "Data") { NumericValue = 8 },
            new("Memory", "System memory", "Memory Available", "8.00 GB", "Data") { NumericValue = 8 },
            new("Memory", "DIMM #1", "Temperature", "42.00 C", "Temperature") { NumericValue = 42 },
            new("Network", "Ethernet", "Upload", "10.00 MB", "Throughput")
        };

        var selected = HardwareMonitorService.SelectEssentialReadings(readings);

        Assert.Contains(selected, r => r.Category == "CPU" && r.Sensor == "CPU Total");
        Assert.Contains(selected, r => r.Category == "GPU" && r.Sensor == "GPU Memory Used");
        Assert.Contains(selected, r => r.Category == "Memory" && r.Sensor == "Memory Used");
        Assert.Contains(selected, r => r.Category == "Memory" && r.SensorType == "Temperature");
        Assert.DoesNotContain(selected, r => r.Sensor == "Core 0");
        Assert.DoesNotContain(selected, r => r.Category == "Network");
        Assert.True(selected.Count < readings.Count);
    }
}

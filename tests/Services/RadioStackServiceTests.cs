using KalOS.Services;

namespace KalOS.Tests.Services;

/// <summary>
/// Guards the service/driver tables and registry mapping used by the deep
/// Bluetooth/Wi-Fi toggle. A wrong service name silently disables nothing,
/// so these tables are contract-tested.
/// </summary>
public class RadioStackServiceTests
{
    [Fact]
    public void BluetoothServices_ContainsTheFullDriverStack()
    {
        // Services
        Assert.Contains("bthserv", RadioStackService.BluetoothServices.Keys);             // Bluetooth Support Service
        Assert.Contains("BluetoothUserService", RadioStackService.BluetoothServices.Keys);
        Assert.Contains("BTAGService", RadioStackService.BluetoothServices.Keys);         // Audio Gateway
        Assert.Contains("BthAvctpSvc", RadioStackService.BluetoothServices.Keys);         // AVCTP

        // Driver stack
        Assert.Contains("HidBth", RadioStackService.BluetoothServices.Keys);              // HID driver
        Assert.Contains("Microsoft_Bluetooth_AvrcpTransport", RadioStackService.BluetoothServices.Keys);
        Assert.Contains("BthEnum", RadioStackService.BluetoothServices.Keys);             // enumerator
        Assert.Contains("BthHFEnum", RadioStackService.BluetoothServices.Keys);
        Assert.Contains("BthLEEnum", RadioStackService.BluetoothServices.Keys);
        Assert.Contains("BthMini", RadioStackService.BluetoothServices.Keys);
        Assert.Contains("BTHMODEM", RadioStackService.BluetoothServices.Keys);
        Assert.Contains("BTHPORT", RadioStackService.BluetoothServices.Keys);             // port driver
        Assert.Contains("BTHUSB", RadioStackService.BluetoothServices.Keys);              // USB driver
        Assert.Contains("RFCOMM", RadioStackService.BluetoothServices.Keys);              // RFCOMM protocol
    }

    [Fact]
    public void WifiServices_ContainsTheWirelessStack()
    {
        Assert.Contains("WlanSvc", RadioStackService.WifiServices.Keys);     // WLAN AutoConfig
        Assert.Contains("WwanSvc", RadioStackService.WifiServices.Keys);     // WWAN AutoConfig
        Assert.Contains("NativeWifiP", RadioStackService.WifiServices.Keys); // NativeWiFi protocol driver
    }

    [Fact]
    public void BluetoothDefaults_ServicesAuto_DriversDemand()
    {
        Assert.Equal(2, RadioStackService.BluetoothServices["bthserv"]);
        Assert.Equal(2, RadioStackService.BluetoothServices["BluetoothUserService"]);
        Assert.Equal(3, RadioStackService.BluetoothServices["BTHPORT"]);
        Assert.Equal(3, RadioStackService.BluetoothServices["BTHUSB"]);
        Assert.Equal(3, RadioStackService.BluetoothServices["BthEnum"]);
        Assert.Equal(3, RadioStackService.BluetoothServices["RFCOMM"]);
    }

    [Fact]
    public void WifiDefaults_AllAuto()
    {
        Assert.Equal(2, RadioStackService.WifiServices["WlanSvc"]);
        Assert.Equal(2, RadioStackService.WifiServices["WwanSvc"]);
        Assert.Equal(2, RadioStackService.WifiServices["NativeWifiP"]);
    }

    [Theory]
    [InlineData(2, "auto")]
    [InlineData(3, "demand")]
    [InlineData(4, "disabled")]
    [InlineData(1, "demand")]
    [InlineData(99, "demand")]
    public void StartValueName_MapsRegistryStartToScSyntax(int start, string expected)
    {
        Assert.Equal(expected, RadioStackService.StartValueName(start));
    }
}

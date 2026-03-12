using System.Runtime.InteropServices;

namespace USBShare.Services.Native;

/// <summary>
/// SetupAPI.dll P/Invoke 声明
/// 用于设备安装和枚举
/// </summary>
internal static class SetupApi
{
    private const string SetupApiDll = "setupapi.dll";

    #region Device Info Set

    /// <summary>
    /// 创建设备信息集，包含符合指定条件的设备
    /// </summary>
    [DllImport(SetupApiDll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid ClassGuid,
        IntPtr Enumerator,
        IntPtr hwndParent,
        uint Flags);

    /// <summary>
    /// 销毁设备信息集
    /// </summary>
    [DllImport(SetupApiDll, SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(
        IntPtr DeviceInfoSet);

    #endregion

    #region Device Info Data

    /// <summary>
    /// 枚举设备信息集中的设备
    /// </summary>
    [DllImport(SetupApiDll, SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInfo(
        IntPtr DeviceInfoSet,
        uint MemberIndex,
        ref SP_DEVINFO_DATA DeviceInfoData);

    #endregion

    #region Device Properties

    /// <summary>
    /// 获取设备属性
    /// </summary>
    [DllImport(SetupApiDll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceProperty(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        ref DEVPROPKEY PropertyKey,
        out uint PropertyType,
        IntPtr PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize,
        uint Flags);

    /// <summary>
    /// 获取设备注册表属性
    /// </summary>
    [DllImport(SetupApiDll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData,
        uint Property,
        out uint PropertyRegDataType,
        IntPtr PropertyBuffer,
        uint PropertyBufferSize,
        out uint RequiredSize);

    #endregion

    #region Constants

    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    public const uint DIGCF_ALLCLASSES = 0x00000004;

    // SPDRP_ constants for SetupDiGetDeviceRegistryProperty
    public const uint SPDRP_DEVICEDESC = 0x00000000;  // DeviceDesc (R/W)
    public const uint SPDRP_HARDWAREID = 0x00000001;  // HardwareID (R/W)
    public const uint SPDRP_COMPATIBLEIDS = 0x00000002;  // CompatibleIDs (R/W)
    public const uint SPDRP_SERVICE = 0x00000004;  // Service (R/W)
    public const uint SPDRP_CLASS = 0x00000007;  // Class (R/W)
    public const uint SPDRP_CLASSGUID = 0x00000008;  // ClassGUID (R/W)
    public const uint SPDRP_DRIVER = 0x00000009;  // Driver (R/W)
    public const uint SPDRP_CONFIGFLAGS = 0x0000000A;  // ConfigFlags (R/W)
    public const uint SPDRP_MFG = 0x0000000B;  // Mfg (R/W)
    public const uint SPDRP_FRIENDLYNAME = 0x0000000C;  // FriendlyName (R/W)
    public const uint SPDRP_LOCATION_INFORMATION = 0x0000000D;  // LocationInformation (R/W)
    public const uint SPDRP_PHYSICAL_DEVICE_OBJECT_NAME = 0x0000000E;  // PDO Name (R)
    public const uint SPDRP_CAPABILITIES = 0x0000000F;  // Capabilities (R)
    public const uint SPDRP_UI_NUMBER = 0x00000010;  // UiNumber (R)
    public const uint SPDRP_UPPERFILTERS = 0x00000011;  // UpperFilters (R/W)
    public const uint SPDRP_LOWERFILTERS = 0x00000012;  // LowerFilters (R/W)
    public const uint SPDRP_BUSTYPEGUID = 0x00000013;  // BusTypeGUID (R)
    public const uint SPDRP_LEGACYBUSTYPE = 0x00000014;  // LegacyBusType (R)
    public const uint SPDRP_BUSNUMBER = 0x00000015;  // BusNumber (R)
    public const uint SPDRP_ENUMERATOR_NAME = 0x00000016;  // Enumerator Name (R)
    public const uint SPDRP_SECURITY = 0x00000017;  // Security (R/W, binary form)
    public const uint SPDRP_SECURITY_SDS = 0x00000018;  // Security (W, SDS form)
    public const uint SPDRP_DEVTYPE = 0x00000019;  // Device Type (R/W)
    public const uint SPDRP_EXCLUSIVE = 0x0000001A;  // Device is exclusive-access (R/W)
    public const uint SPDRP_CHARACTERISTICS = 0x0000001B;  // Device characteristics (R/W)
    public const uint SPDRP_ADDRESS = 0x0000001C;  // Device Address (R)
    public const uint SPDRP_UI_NUMBER_DESC_FORMAT = 0x0000001D;  // UiNumberDescFormat (R/W)
    public const uint SPDRP_DEVICE_POWER_DATA = 0x0000001E;  // Device Power Data (R)
    public const uint SPDRP_REMOVAL_POLICY = 0x0000001F;  // Removal Policy (R)
    public const uint SPDRP_REMOVAL_POLICY_HW_DEFAULT = 0x00000020;  // Hardware Removal Policy (R)
    public const uint SPDRP_REMOVAL_POLICY_OVERRIDE = 0x00000021;  // Removal Policy Override (RW)
    public const uint SPDRP_INSTALL_STATE = 0x00000022;  // Device Install State (R)
    public const uint SPDRP_LOCATION_PATHS = 0x00000023;  // Device Location Paths (R)

    #endregion

    #region Structures

    /// <summary>
    /// 设备信息数据结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;  // DEVINST type is uint
        public ulong Reserved;
    }

    /// <summary>
    /// 设备属性键结构
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct DEVPROPKEY
    {
        public readonly Guid fmtid;
        public readonly uint pid;

        public DEVPROPKEY(Guid fmtid, uint pid)
        {
            this.fmtid = fmtid;
            this.pid = pid;
        }
    }

    #endregion

    #region Device Property Keys

    /// <summary>
    /// 设备属性键
    /// </summary>
    public static class DevPropKeys
    {
        /// <summary>
        /// DEVPKEY_Device_FriendlyName
        /// </summary>
        public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(
            new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 4);

        /// <summary>
        /// DEVPKEY_Device_Parent
        /// </summary>
        public static readonly DEVPROPKEY DEVPKEY_Device_Parent = new(
            new Guid(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x81, 0x28, 0x7a), 8);
    }

    #endregion
}

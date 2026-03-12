using System.Runtime.InteropServices;
using System.Text;

namespace USBShare.Services.Native;

/// <summary>
/// CfgMgr32.dll P/Invoke 声明
/// 用于获取设备管理器中的设备信息
/// </summary>
internal static class CfgMgr32
{
    private const string CfgMgr32Dll = "CfgMgr32.dll";

    #region Device ID List

    /// <summary>
    /// 获取指定类别的设备实例 ID 列表
    /// </summary>
    /// <param name="pszClassGuid">设备类别的 GUID 或枚举器名称，null 表示所有设备</param>
    /// <param name="Buffer">接收设备 ID 列表的缓冲区</param>
    /// <param name="bufferLen">缓冲区大小（字符数）</param>
    /// <param name="ulFlags">标志位</param>
    /// <returns>
    /// CR_SUCCESS = 0x00000000 成功
    /// CR_BUFFER_SMALL = 0x0000001A 缓冲区太小
    /// </returns>
    [DllImport(CfgMgr32Dll, SetLastError = false, CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW", ExactSpelling = true)]
    public static extern uint CM_Get_Device_ID_List(
        string? pszClassGuid,
        [Out] char[] Buffer,
        uint bufferLen,
        uint ulFlags);

    /// <summary>
    /// 获取设备实例 ID 列表所需缓冲区长度（以字符计）。
    /// </summary>
    [DllImport(CfgMgr32Dll, SetLastError = false, CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW", ExactSpelling = true)]
    public static extern uint CM_Get_Device_ID_List_Size(
        out uint pulLen,
        string? pszFilter,
        uint ulFlags);

    /// <summary>
    /// 释放 CM_Get_Device_ID_List 分配的内存
    /// </summary>
    [DllImport(CfgMgr32Dll, SetLastError = true)]
    public static extern uint CM_Free_Device_ID_List(
        [Out] char[] Buffer);

    #endregion

    #region Device Node Operations

    /// <summary>
    /// 通过设备实例 ID 定位设备节点
    /// </summary>
    /// <param name="pszDeviceID">设备实例 ID</param>
    /// <param name="dnDevInst">输出的设备节点句柄</param>
    /// <param name="ulFlags">必须为 0</param>
    /// <returns>CR_SUCCESS (0) 表示成功</returns>
    [DllImport(CfgMgr32Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint CM_Locate_DevNode(
        out uint dnDevInst,
        string pszDeviceID,
        uint ulFlags);

    /// <summary>
    /// 获取设备的父设备节点
    /// </summary>
    /// <param name="dnDevInst">当前设备节点</param>
    /// <param name="dnDevInstParent">输出的父设备节点</param>
    /// <param name="ulFlags">必须为 0</param>
    /// <returns>CR_SUCCESS (0) 表示成功</returns>
    [DllImport(CfgMgr32Dll, SetLastError = true)]
    public static extern uint CM_Get_Parent(
        out uint dnDevInstParent,
        uint dnDevInst,
        uint ulFlags);

    /// <summary>
    /// 获取设备节点的设备实例 ID
    /// </summary>
    /// <param name="dnDevInst">设备节点</param>
    /// <param name="Buffer">接收 ID 的缓冲区</param>
    /// <param name="pulLength">输入时为缓冲区大小，输出时为实际长度</param>
    /// <param name="ulFlags">必须为 0</param>
    /// <returns>CR_SUCCESS (0) 表示成功</returns>
    [DllImport(CfgMgr32Dll, SetLastError = false, CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_Device_ID(
        uint dnDevInst,
        StringBuilder Buffer,
        uint ulLength,
        uint ulFlags);

    #endregion

    #region Device Properties

    /// <summary>
    /// 获取设备属性
    /// </summary>
    /// <param name="dnDevInst">设备节点</param>
    /// <param name="key">属性键</param>
    /// <param name="ulPropertyType">属性类型</param>
    /// <param name="Buffer">接收属性值的缓冲区</param>
    /// <param name="pulLength">输入时为缓冲区大小，输出时为实际大小</param>
    /// <param name="ulFlags">必须为 0</param>
    /// <returns>CR_SUCCESS (0) 表示成功</returns>
    [DllImport(CfgMgr32Dll, SetLastError = true)]
    public static extern uint CM_Get_DevNode_Property(
        uint dnDevInst,
        in DEVPROPKEY key,
        out uint ulPropertyType,
        [Out] byte[] Buffer,
        ref uint pulLength,
        uint ulFlags);

    /// <summary>
    /// 获取设备属性（字符串版本）
    /// </summary>
    [DllImport(CfgMgr32Dll, SetLastError = false, CharSet = CharSet.Unicode)]
    public static extern uint CM_Get_DevNode_PropertyW(
        uint dnDevInst,
        in DEVPROPKEY key,
        out uint ulPropertyType,
        StringBuilder Buffer,
        ref uint pulLength,
        uint ulFlags);

    #endregion

    #region Constants

    public const uint CR_SUCCESS = 0x00000000;
    public const uint CR_BUFFER_SMALL = 0x0000001A;
    public const uint CR_INVALID_DEVINST = 0x0000001F;
    public const uint CR_INVALID_DEVICE_ID = 0x00000028;

    /// <summary>
    /// CM_Get_Device_ID_List 的 ulFlags 参数
    /// </summary>
    public const uint CM_GET_DEVICE_INTERFACE_LIST_PRESENT = 0x00000000;

    /// <summary>
    /// 属性类型
    /// </summary>
    public const uint DEVPROP_TYPE_STRING = 0x00000012;  // UTF-16 string
    public const uint DEVPROP_TYPE_EMPTY = 0x00000000;
    public const uint DEVPROP_TYPE_NULL = 0x00000001;
    public const uint DEVPROP_TYPE_UINT32 = 0x00000007;
    public const uint DEVPROP_TYPE_UINT64 = 0x0000000B;
    public const uint DEVPROP_TYPE_BOOLEAN = 0x0000000D;

    #endregion

    #region Structures

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
    /// 设备属性键定义
    /// 参考: https://learn.microsoft.com/en-us/windows-hardware/drivers/install/device-property-keys
    /// </summary>
    public static class DevicePropertyKeys
    {
        private static readonly Guid DevPropKeySystem = new(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x81, 0x28, 0x7a);

        /// <summary>
        /// DEVPKEY_Device_FriendlyName - 设备友好名称
        /// </summary>
        public static readonly DEVPROPKEY DEVPKEY_Device_FriendlyName = new(
            new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 4);

        /// <summary>
        /// DEVPKEY_Device_Class - 设备类
        /// </summary>
        public static readonly DEVPROPKEY DEVPKEY_Device_Class = new(
            new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 9);

        /// <summary>
        /// DEVPKEY_Device_Parent - 父设备实例 ID
        /// </summary>
        public static readonly DEVPROPKEY DEVPKEY_Device_Parent = new(
            new Guid(0x4340a6c5, 0x93fa, 0x4706, 0x97, 0x2c, 0x7b, 0x64, 0x80, 0x81, 0x28, 0x7a), 8);

        /// <summary>
        /// DEVPKEY_Device_Manufacturer - 制造商
        /// </summary>
        public static readonly DEVPROPKEY DEVPKEY_Device_Manufacturer = new(
            new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0), 13);

        /// <summary>
        /// DEVPKEY_Name - 设备名称
        /// </summary>
        public static readonly DEVPROPKEY DEVPKEY_Name = new(DevPropKeySystem, 10);
    }

    #endregion
}

using System.Runtime.InteropServices;

namespace AnsiDotNet;

internal static unsafe partial class Libc
{
    private const string Lib = "libc";

    public const int EINTR = 4;
    public const int EAGAIN_LINUX = 11;
    public const int EAGAIN_BSD = 35;
    public const short POLLIN = 0x1;
    public const short POLLOUT = 0x4;
    public const short POLLERR = 0x8;
    public const short POLLHUP = 0x10;
    public const short POLLNVAL = 0x20;
    public const int TCSANOW = 0;
    public const int TCSADRAIN = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short REvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize
    {
        public ushort Rows;
        public ushort Cols;
        public ushort XPixel;
        public ushort YPixel;
    }

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int isatty(int fd);

    // termios is treated as an opaque blob; its layout differs between platforms.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int tcgetattr(int fd, byte* termios);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int tcsetattr(int fd, int optionalActions, byte* termios);

    [LibraryImport(Lib)]
    public static partial void cfmakeraw(byte* termios);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int poll(PollFd* fds, nuint nfds, int timeout);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint read(int fd, byte* buf, nint count);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint write(int fd, byte* buf, nint count);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int pipe(int* fds);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int close(int fd);

    // Variadic in C; only called on platforms where passing the pointer as a fixed argument is ABI-compatible.
    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ioctl(int fd, nuint request, WinSize* arg);
}

internal static unsafe partial class Kernel32
{
    private const string Lib = "kernel32.dll";

    public const int STD_INPUT_HANDLE = -10;
    public const int STD_OUTPUT_HANDLE = -11;

    public const uint ENABLE_PROCESSED_INPUT = 0x0001;
    public const uint ENABLE_LINE_INPUT = 0x0002;
    public const uint ENABLE_ECHO_INPUT = 0x0004;
    public const uint ENABLE_WINDOW_INPUT = 0x0008;
    public const uint ENABLE_MOUSE_INPUT = 0x0010;
    public const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    public const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    public const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    public const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    public const ushort KEY_EVENT = 0x0001;
    public const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;

    public const uint WAIT_OBJECT_0 = 0;

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct InputRecord
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(14)] public char UnicodeChar;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScreenBufferInfo
    {
        public short SizeX, SizeY;
        public short CursorX, CursorY;
        public ushort Attributes;
        public short Left, Top, Right, Bottom;
        public short MaxX, MaxY;
    }

    [LibraryImport(Lib, SetLastError = true)]
    public static partial nint GetStdHandle(int which);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int GetConsoleMode(nint handle, uint* mode);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int SetConsoleMode(nint handle, uint mode);

    [LibraryImport(Lib)]
    public static partial uint GetConsoleOutputCP();

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int SetConsoleOutputCP(uint codePage);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int GetConsoleScreenBufferInfo(nint handle, ScreenBufferInfo* info);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int PeekConsoleInputW(nint handle, InputRecord* buffer, uint length, uint* read);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ReadConsoleInputW(nint handle, InputRecord* buffer, uint length, uint* read);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int ReadConsoleW(nint handle, char* buffer, uint toRead, uint* read, void* control);

    [LibraryImport(Lib, SetLastError = true)]
    public static partial int WriteFile(nint handle, byte* buffer, uint toWrite, uint* written, void* overlapped);
}

using SnowWindow.App;
using SnowWindow.Platform;

namespace SnowWindow;

internal static class Program
{
    private const string MutexName = @"Local\SnowWindow.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        // 창 좌표·커서 좌표·오버레이 크기를 모두 물리 픽셀로 맞추려면 어떤 창보다 먼저 Per-Monitor V2로 바꿔야 한다.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        if (args.Contains("--dump-windows"))
        {
            // 진단용: 눈이 쌓이는/가리는 창으로 인식된 목록을 출력한다.
            // 예) SnowWindow.exe --dump-windows > windows.txt
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Out.Write(new WindowTracker().Describe());
            return 0;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Snow Window가 이미 실행 중입니다.\n작업 표시줄 알림 영역의 눈송이 아이콘을 확인하세요.",
                "Snow Window", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI 스레드 처리되지 않은 예외", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("처리되지 않은 예외로 종료합니다", e.ExceptionObject as Exception);

        Log.Info($"시작 v{typeof(Program).Assembly.GetName().Version} ({Environment.ProcessPath})");
        Application.Run(new TrayApp());
        return 0;
    }
}

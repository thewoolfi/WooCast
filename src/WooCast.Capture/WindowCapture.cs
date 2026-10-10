using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

// Псевдонимы обязательны: внутри пространства имён проекта короткое имя Windows
// указывает на наш собственный класс перечисления окон, а не на системное пространство.
using StorageBuffer = Windows.Storage.Streams.Buffer;
using StorageReader = Windows.Storage.Streams.DataReader;

namespace WooCast.Capture;

/// <summary>Кадр окна, снятый системным захватом.</summary>
/// <param name="Pixels">Пиксели в порядке BGRA, построчно, без выравнивания.</param>
/// <param name="Width">Ширина кадра.</param>
/// <param name="Height">Высота кадра.</param>
/// <param name="ArrivedAt">Момент получения кадра (локальное время процесса).</param>
public readonly record struct WindowFrame(byte[] Pixels, int Width, int Height, DateTime ArrivedAt);

/// <summary>
/// Захват содержимого окна через Windows.Graphics.Capture.
/// </summary>
/// <remarks>
/// Система отдаёт содержимое окна напрямую, поэтому перекрытие, сворачивание и уход окна
/// в фон картинку не портят: прежний способ (обрезка снимка рабочего стола) показывал то,
/// что лежит сверху, и именно из-за этого «выбор окна» работал не так, как ожидается.
/// <para>
/// Кадры приходят текстурами Direct3D. Их приходится копировать в память: кодировщик
/// ffmpeg принимает поток сырых пикселей, а не видеопамять. Копия выполняется средствами
/// системы (<see cref="SoftwareBitmap.CreateCopyFromSurfaceAsync"/>), без собственного
/// взаимодействия с Direct3D — меньше кода и меньше поводов для ошибок.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowCapture : IDisposable
{
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly IDirect3DDevice _graphicsDevice;
    private readonly Action<string>? _onClosed;
    private Action<WindowFrame> _onFrame;

    private SizeInt32 _poolSize;
    private readonly int _framesPerSecond;

    private nint _d3dDevice;
    private nint _dxgiDevice;
    private nint _inspectableDevice;
    private int _busy;
    private long _framesTaken;
    private long _framesSkipped;
    private long _frameEvents;
    private long _emptyPolls;
    private string? _lastError;
    private bool _disposed;

    private WindowCapture(
        Direct3D11CaptureFramePool pool,
        GraphicsCaptureSession session,
        IDirect3DDevice graphicsDevice,
        WindowFrame firstFrame,
        Action<WindowFrame> onFrame,
        Action<string>? onClosed,
        int framesPerSecond,
        nint d3dDevice,
        nint dxgiDevice,
        nint inspectableDevice)
    {
        _pool = pool;
        _session = session;
        _graphicsDevice = graphicsDevice;
        _onFrame = onFrame;
        _onClosed = onClosed;
        _framesPerSecond = framesPerSecond;
        _d3dDevice = d3dDevice;
        _dxgiDevice = dxgiDevice;
        _inspectableDevice = inspectableDevice;
        _poolSize = new SizeInt32 { Width = firstFrame.Width, Height = firstFrame.Height };
    }

    /// <summary>Сколько кадров отдано обработчику.</summary>
    public long FramesTaken => Interlocked.Read(ref _framesTaken);

    /// <summary>Сколько кадров пропущено: обработчик не успевал за потоком.</summary>
    public long FramesSkipped => Interlocked.Read(ref _framesSkipped);

    /// <summary>Сколько раз сработало событие появления кадра.</summary>
    public long FrameEvents => Interlocked.Read(ref _frameEvents);

    /// <summary>Сколько раз событие сработало, но кадра не оказалось.</summary>
    public long EmptyPolls => Interlocked.Read(ref _emptyPolls);

    /// <summary>
    /// Начать захват окна.
    /// </summary>
    /// <param name="windowHandle">Дескриптор окна.</param>
    /// <param name="onFrame">Обработчик кадров; вызывается на потоке захвата.</param>
    /// <param name="onClosed">Сообщение о закрытии окна.</param>
    /// <param name="error">Причина отказа, если начать не удалось.</param>
    /// <returns>Захват или <see langword="null"/>, если окно захватить нельзя.</returns>
    public static WindowCapture? TryStart(
        nint windowHandle,
        Action<WindowFrame> onFrame,
        out string error,
        Action<string>? onClosed = null,
        Action<string>? onWarning = null,
        bool showCursor = true)
    {
        error = string.Empty;

        if (!WindowCaptureSupport.IsSupported())
        {
            error = "системный захват окна недоступен";
            return null;
        }

        if (windowHandle == 0)
        {
            error = "не передан дескриптор окна";
            return null;
        }

        nint d3dDevice = 0;
        nint dxgiDevice = 0;
        nint inspectable = 0;

        // Консольное приложение не инициализирует COM само: без этого обращения
        // к системному захвату заканчиваются отказом.
        TryInitializeCom();

        try
        {
            if (!TryCreateDevice(out d3dDevice, out dxgiDevice, out inspectable, out error))
            {
                return null;
            }

            var graphicsDevice = WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);
            var item = CreateItemForWindow(windowHandle, out var itemError);

            if (item is null)
            {
                ReleaseDevice(d3dDevice, dxgiDevice, inspectable);
                error = itemError;
                return null;
            }

            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                ReleaseDevice(d3dDevice, dxgiDevice, inspectable);
                error = $"окно имеет нулевой размер ({size.Width}×{size.Height})";
                return null;
            }

            // Буферов два: пока один копируется в память, система наполняет второй.
            var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                graphicsDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);

            var session = pool.CreateCaptureSession(item);

            // Курсор системы не захватываем: свой рисует ffmpeg, иначе он был бы двойным.
            ApplyCursorCapture(session, showCursor, onWarning);

            var capture = new WindowCapture(
                pool,
                session,
                graphicsDevice,
                new WindowFrame([], size.Width, size.Height, DateTime.UtcNow),
                onFrame,
                onClosed,
                0,
                d3dDevice,
                dxgiDevice,
                inspectable);

            pool.FrameArrived += capture.OnFrameArrived;
            item.Closed += capture.OnItemClosed;
            session.StartCapture();

            return capture;
        }
        catch (Exception ex)
        {
            ReleaseDevice(d3dDevice, dxgiDevice, inspectable);
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// Заменить обработчик кадров.
    /// </summary>
    /// <remarks>
    /// Нужно потому, что кодировщику размер кадра сообщают при запуске: сначала кадр
    /// берётся для определения размера, и только затем назначается постоянный обработчик,
    /// который пишет кадры в кодировщик.
    /// </remarks>
    public void SetFrameHandler(Action<WindowFrame> onFrame) => _onFrame = onFrame;

    /// <summary>Остановить захват и освободить ресурсы.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _pool.FrameArrived -= OnFrameArrived;
        }
        catch (Exception)
        {
            // Отписка от события не должна мешать освобождению.
        }

        try
        {
            _session.Dispose();
            _pool.Dispose();
        }
        catch (Exception)
        {
            // Закрытие окна могло уже разрушить сеанс.
        }

        ReleaseDevice(_d3dDevice, _dxgiDevice, _inspectableDevice);
        _d3dDevice = 0;
        _dxgiDevice = 0;
        _inspectableDevice = 0;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        _onClosed?.Invoke("окно закрыто");
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Interlocked.Increment(ref _frameEvents);

        // Кадры приходят быстрее, чем успевает копирование в память. Пропускаем лишние,
        // а не копим их: очередь захвата не должна расти и добавлять задержку.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            Interlocked.Increment(ref _framesSkipped);

            // Кадр нужно забрать и освободить, иначе пул не отдаст следующий.
            try
            {
                using var pending = sender.TryGetNextFrame();
            }
            catch (Exception)
            {
                // Пул мог быть закрыт.
            }

            Interlocked.Exchange(ref _busy, 0);
            return;
        }

        var stage = "начало";

        try
        {
            stage = "получение кадра";
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                Interlocked.Increment(ref _emptyPolls);
                return;
            }

            stage = "размер кадра";
            var width = frame.ContentSize.Width;
            var height = frame.ContentSize.Height;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            // Окно изменило размер: пул кадров привязан к прежнему размеру, и без
            // пересоздания кадры просто перестают приходить. Проверено на живом окне:
            // после уменьшения пришёл один кадр и поток замолчал.
            if (width != _poolSize.Width || height != _poolSize.Height)
            {
                stage = "пересоздание пула кадров";
                var newSize = new SizeInt32 { Width = width, Height = height };

                _pool.Recreate(
                    _graphicsDevice,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    newSize);

                _poolSize = newSize;
            }

            stage = "поверхность кадра";
            var surface = frame.Surface;
            if (surface is null)
            {
                Interlocked.CompareExchange(ref _lastError, "кадр пришёл без поверхности", null);
                return;
            }

            // Копирование асинхронное, но кадр живёт только внутри этого вызова: сначала
            // получаем SoftwareBitmap целиком, затем читаем пиксели.
            stage = "копирование в память";
            var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(surface)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            if (bitmap is null)
            {
                Interlocked.CompareExchange(
                    ref _lastError,
                    "система не отдала изображение кадра",
                    null);
                return;
            }

            stage = "чтение пикселей";
            var pixels = ReadPixels(bitmap, width, height, out var readError);
            if (pixels is null)
            {
                Interlocked.CompareExchange(
                    ref _lastError,
                    readError.Length > 0 ? readError : "пиксели кадра недоступны",
                    null);
                return;
            }

            Interlocked.Increment(ref _framesTaken);
            _onFrame(new WindowFrame(pixels, width, height, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            // Сбой на одном кадре не должен останавливать захват, но и молчать о нём
            // нельзя: именно так теряются причины. Первая ошибка сохраняется и
            // показывается снаружи — вместе с этапом, на котором она случилась.
            Interlocked.CompareExchange(
                ref _lastError,
                $"{stage}: {ex.GetType().Name}: {ex.Message}",
                null);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>Первая ошибка обработки кадра, если она была.</summary>
    public string? LastError => _lastError;

    /// <summary>
    /// Прочитать пиксели изображения в плотный массив BGRA.
    /// </summary>
    /// <remarks>
    /// Чтение идёт штатными средствами WinRT: раньше здесь был доступ к памяти буфера
    /// через COM-интерфейс, и маршалинг указателя падал. Способ без COM надёжнее
    /// и не зависит от встроенного COM-взаимодействия .NET.
    /// <para>
    /// Строка в буфере может быть длиннее кадра (выравнивание), а кодировщику нужен
    /// плотный поток — поэтому строки при необходимости пересобираются.
    /// </para>
    /// </remarks>
    private static byte[]? ReadPixels(SoftwareBitmap bitmap, int width, int height, out string error)
    {
        error = string.Empty;

        var rowBytes = width * 4;
        int stride;

        using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
        {
            if (buffer is null)
            {
                error = "буфер изображения недоступен";
                return null;
            }

            stride = buffer.GetPlaneDescription(0).Stride;
        }

        var total = (long)stride * height;
        if (stride < rowBytes || total <= 0 || total > int.MaxValue)
        {
            error = $"неожиданный размер строки изображения ({stride} при ширине {width})";
            return null;
        }

        var raw = new byte[total];

        try
        {
            var streamBuffer = new StorageBuffer((uint)total);
            streamBuffer.Length = (uint)total;
            bitmap.CopyToBuffer(streamBuffer);

            using var reader = StorageReader.FromBuffer(streamBuffer);
            reader.ReadBytes(raw);
        }
        catch (Exception ex)
        {
            error = $"чтение буфера: {ex.GetType().Name}: {ex.Message}";
            return null;
        }

        if (stride == rowBytes)
        {
            return raw;
        }

        var pixels = new byte[(long)rowBytes * height];

        for (var y = 0; y < height; y++)
        {
            Array.Copy(raw, y * stride, pixels, y * rowBytes, rowBytes);
        }

        return pixels;
    }

    /// <summary>
    /// Включить или выключить курсор системы в кадре.
    /// </summary>
    /// <remarks>
    /// Раньше курсор здесь всегда отключался — с расчётом, что его нарисует ffmpeg.
    /// Это верно только для съёмки рабочего стола: при системном захвате окна ffmpeg
    /// ничего не рисует, он только кодирует готовые кадры, и курсор не рисовал никто.
    /// Поэтому настройка «Показывать курсор» не давала результата.
    /// <para>
    /// Свойство доступно только с определённой версии системы; если его нет,
    /// это не ошибка — в кадре просто останется курсор, как решит система.
    /// </para>
    /// </remarks>
    private static void ApplyCursorCapture(
        GraphicsCaptureSession session,
        bool showCursor,
        Action<string>? onWarning)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
            {
                session.IsCursorCaptureEnabled = showCursor;
            }
        }
        catch (Exception ex)
        {
            onWarning?.Invoke($"не удалось настроить показ курсора: {ex.Message}");
        }
    }

    private static bool TryCreateDevice(
        out nint device,
        out nint dxgiDevice,
        out nint inspectable,
        out string error)
    {
        device = 0;
        dxgiDevice = 0;
        inspectable = 0;
        error = string.Empty;

        // Флаг BGRA обязателен: без него система не отдаёт кадры в этом формате.
        const uint BgraSupport = 0x20;
        const int SdkVersion = 7;

        var hr = D3D11CreateDevice(
            0,
            D3DDriverType.Hardware,
            0,
            BgraSupport,
            0,
            0,
            SdkVersion,
            out device,
            out _,
            out var context);

        if (hr < 0)
        {
            error = $"не удалось создать устройство Direct3D (0x{hr:X8})";
            return false;
        }

        // Контекст нужен только для создания устройства; дальше работа идёт через пул кадров.
        if (context != 0)
        {
            Marshal.Release(context);
        }

        var dxgiIid = typeof(IDXGIDevice).GUID;
        hr = Marshal.QueryInterface(device, in dxgiIid, out dxgiDevice);

        if (hr < 0)
        {
            error = $"устройство не поддерживает DXGI (0x{hr:X8})";
            Marshal.Release(device);
            device = 0;
            return false;
        }

        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out inspectable);

        if (hr < 0)
        {
            error = $"не удалось получить устройство для захвата (0x{hr:X8})";
            Marshal.Release(dxgiDevice);
            Marshal.Release(device);
            device = 0;
            dxgiDevice = 0;
            return false;
        }

        return true;
    }

    private static void TryInitializeCom()
    {
        try
        {
            // RO_INIT_MULTITHREADED: захват идёт с потока пула, однопоточная модель
            // здесь не подходит. Повторный вызов безвреден.
            _ = RoInitialize(1);
        }
        catch (Exception)
        {
            // Если COM уже инициализирован другим способом, отказ не мешает работе.
        }
    }

    private static void ReleaseDevice(nint device, nint dxgiDevice, nint inspectable)
    {
        if (inspectable != 0)
        {
            Marshal.Release(inspectable);
        }

        if (dxgiDevice != 0)
        {
            Marshal.Release(dxgiDevice);
        }

        if (device != 0)
        {
            Marshal.Release(device);
        }
    }

    private static GraphicsCaptureItem? CreateItemForWindow(nint windowHandle, out string error)
    {
        error = string.Empty;

        var factory = GetCaptureItemFactory(out var factoryError);
        if (factory is null)
        {
            error = factoryError;
            return null;
        }

        try
        {
            // Идентификатор интерфейса элемента захвата. Взят из заголовка Windows SDK
            // (windows.graphics.capture.h): у классов WinRT `typeof(...).GUID` пуст,
            // а вычислитель идентификаторов проекции разбирает атрибут не всегда.
            var iid = IidGraphicsCaptureItem;
            var pointer = CreateForWindowUnsafe(factory.Pointer, windowHandle, ref iid, out var hr);

            if (pointer == 0)
            {
                error = $"система отказала в создании элемента захвата (0x{hr:X8})";
                return null;
            }

            try
            {
                return WinRT.MarshalInspectable<GraphicsCaptureItem>.FromAbi(pointer);
            }
            finally
            {
                Marshal.Release(pointer);
            }
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            Marshal.Release(factory.Pointer);
        }
    }

    /// <summary>
    /// Вызвать <c>IGraphicsCaptureItemInterop::CreateForWindow</c> напрямую.
    /// </summary>
    /// <remarks>
    /// Обычное приведение к COM-интерфейсу здесь не работает: встроенное COM-взаимодействие
    /// .NET включено не во всех видах приложений. Прямой вызов через таблицу методов
    /// не зависит от него вовсе. Места в таблице: 0 — QueryInterface, 1 — AddRef,
    /// 2 — Release, 3 — CreateForWindow.
    /// </remarks>
    private static unsafe nint CreateForWindowUnsafe(nint factory, nint windowHandle, ref Guid iid, out int result)
    {
        var table = *(nint**)factory;
        var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)table[3];

        nint item = 0;

        fixed (Guid* iidPointer = &iid)
        {
            result = create(factory, windowHandle, iidPointer, &item);
        }

        return result < 0 ? 0 : item;
    }

    private static ComPointer? GetCaptureItemFactory(out string error)
    {
        error = string.Empty;

        const string ClassName = "Windows.Graphics.Capture.GraphicsCaptureItem";

        // Длина строки берётся из самой строки: ошибка в одном символе делает имя класса
        // недействительным, и фабрика молча не создаётся.
        var hr = WindowsCreateString(ClassName, ClassName.Length, out var className);

        if (hr < 0)
        {
            error = $"не удалось подготовить имя класса (0x{hr:X8})";
            return null;
        }

        try
        {
            var iid = typeof(IGraphicsCaptureItemInteropId).GUID;
            hr = RoGetActivationFactory(className, ref iid, out var factory);

            if (hr < 0 || factory == 0)
            {
                error = $"фабрика захвата недоступна (0x{hr:X8})";
                return null;
            }

            // Указатель возвращается уже приведённым к нужному интерфейсу: держим его
            // как есть и освобождаем вручную.
            return new ComPointer(factory);
        }
        finally
        {
            WindowsDeleteString(className);
        }
    }

    /// <summary>Указатель COM, освобождаемый при закрытии.</summary>
    private sealed class ComPointer(nint pointer)
    {
        public nint Pointer { get; } = pointer;
    }

    /// <summary>
    /// Идентификатор интерфейса <c>IGraphicsCaptureItemInterop</c>.
    /// </summary>
    /// <remarks>
    /// Объявлен отдельно от COM-интерфейса: вызовы идут через таблицу методов,
    /// и встроенное COM-взаимодействие .NET здесь не участвует.
    /// </remarks>
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    private interface IGraphicsCaptureItemInteropId
    {
    }

    /// <summary>Идентификатор интерфейса <c>IGraphicsCaptureItem</c> из заголовков Windows SDK.</summary>
    private static readonly Guid IidGraphicsCaptureItem =
        new("79c3f95b-31f7-4ec2-a464-632ef5d30760");

    /// <summary>Идентификатор интерфейса <c>IDXGIDevice</c>.</summary>
    [Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C")]
    private interface IDXGIDevice
    {
    }

    private static class D3DDriverType
    {
        public const int Hardware = 1;
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        nint adapter,
        int driverType,
        nint software,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        int sdkVersion,
        out nint device,
        out int featureLevel,
        out nint immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length,
        out nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, ref Guid iid, out nint factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoInitialize(int initType);
}

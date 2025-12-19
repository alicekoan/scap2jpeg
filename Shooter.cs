using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Jpeg;
using Image = SixLabors.ImageSharp.Image;

namespace scap2jpeg
{
    internal class Shooter : IDisposable
    {
        private class AdapterInfo
        {
            public uint AdapterIndex { get; set; }
            public required IDXGIAdapter1 Adapter { get; set; }
            public required ID3D11Device Device { get; set; }
            public required ID3D11DeviceContext Context { get; set; }
            public List<OutputInfo> Outputs { get; set; } = [];
        }

        private class OutputInfo
        {
            public uint OutputIndex { get; set; }
            public required IDXGIOutput Output { get; set; }
            public required IDXGIOutput1 Output1 { get; set; }
            public required IDXGIOutputDuplication Duplication { get; set; }
        }

        private readonly Logger _logger;
        private IDXGIFactory1? _factory;
        private readonly List<AdapterInfo> _duplicators = [];
        private readonly string _screenshotsDir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        private bool _disposed = false;

        public Shooter(Logger logger)
        {
            _logger = logger;
            Directory.CreateDirectory(_screenshotsDir);
        }

        ~Shooter()
        {
            Dispose();
        }

        public void Start(CancellationToken token)
        {
            Cleanup();

            if (token.IsCancellationRequested) return;

            _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>() ?? throw new Exception("Failed to create DXGI Factory.");

            uint adapterIndex = 0;
            while (_factory.IsCurrent && !token.IsCancellationRequested)
            {
                Result adapterResult = _factory.EnumAdapters1(adapterIndex++, out IDXGIAdapter1? adapter);
                if (adapterResult.Failure || adapter == null)
                {
                    adapter?.Dispose();
                    break;
                }

                Result deviceResult = D3D11.D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    [],
                    out ID3D11Device? device,
                    out ID3D11DeviceContext? context
                );

                if (deviceResult.Failure || device == null || context == null)
                {
                    context?.Dispose();
                    device?.Dispose();
                    adapter.Dispose();
                    continue;
                }

                AdapterInfo adapterInfo = new()
                {
                    AdapterIndex = adapterIndex,
                    Adapter = adapter,
                    Device = device,
                    Context = context
                };

                uint outputIndex = 0;
                while (_factory.IsCurrent && !token.IsCancellationRequested)
                {
                    Result outputResult = adapter.EnumOutputs(outputIndex++, out IDXGIOutput output);
                    if (outputResult.Failure || output == null)
                    {
                        output?.Dispose();
                        break;
                    }
                    IDXGIOutput1 output1 = output.QueryInterface<IDXGIOutput1>();
                    if (output1 == null)
                    {
                        output.Dispose();
                        continue;
                    }
                    IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);
                    if (duplication == null)
                    {
                        output1.Dispose();
                        output.Dispose();
                        continue;
                    }
                    adapterInfo.Outputs.Add(new OutputInfo
                    {
                        OutputIndex = outputIndex,
                        Output = output,
                        Output1 = output1,
                        Duplication = duplication
                    });
                }

                if (adapterInfo.Outputs.Count > 0)
                {
                    _duplicators.Add(adapterInfo);
                }
                else
                {
                    context.Dispose();
                    device.Dispose();
                    adapter.Dispose();
                }
            }
            if (_duplicators.Count == 0)
            {
                throw new Exception("No valid outputs found for screen capture.");
            }
        }

        public void Capture(CancellationToken token)
        {
            if (_factory == null || !_factory.IsCurrent) throw new Exception("DXGI Factory is not initialized.");

            uint screenshotCount = 0;

            foreach (var adapterInfo in _duplicators)
            {
                foreach (var outputInfo in adapterInfo.Outputs)
                {
                    if (token.IsCancellationRequested) return;

                    IDXGIResource? desktopResource = null;
                    ID3D11Texture2D? texture2D = null;
                    ID3D11Texture2D? stagingTexture = null;

                    try
                    {
                        Result result = outputInfo.Duplication.AcquireNextFrame(500, out _, out desktopResource);
                        if (result.Failure || desktopResource == null)
                        {
                            desktopResource?.Dispose();
                            continue;
                        }

                        texture2D = desktopResource.QueryInterface<ID3D11Texture2D>();
                        if (texture2D == null)
                        {
                            desktopResource.Dispose();
                            continue;
                        }

                        Texture2DDescription textureDesc = texture2D.Description;
                        if (textureDesc.Format != Format.B8G8R8A8_UNorm)
                        {
                            texture2D.Dispose();
                            desktopResource.Dispose();
                            continue;
                        }

                        Texture2DDescription stagingDesc = new(
                            textureDesc.Format,
                            textureDesc.Width,
                            textureDesc.Height,
                            1,
                            1,
                            BindFlags.None,
                            ResourceUsage.Staging,
                            CpuAccessFlags.Read,
                            1,
                            0,
                            ResourceOptionFlags.None
                        );

                        stagingTexture = adapterInfo.Device.CreateTexture2D(stagingDesc);
                        adapterInfo.Context.CopyResource(stagingTexture, texture2D);

                        MappedSubresource mappedResource = adapterInfo.Context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                        Image? image = null;
                        try
                        {
                            uint bytesPerRow = textureDesc.Width * 4;
                            int width = (int)textureDesc.Width;
                            int height = (int)textureDesc.Height;

                            if (bytesPerRow == mappedResource.RowPitch)
                            {
                                unsafe
                                {
                                    ReadOnlySpan<Bgra32> span = new(mappedResource.DataPointer.ToPointer(), width * height);
                                    image = Image.LoadPixelData<Bgra32>(span, width, height);
                                }
                            }
                            else
                            {
                                Bgra32[] pixelData = new Bgra32[width * height];

                                unsafe
                                {
                                    byte* source = (byte*)mappedResource.DataPointer;
                                    fixed (Bgra32* destPtr = pixelData)
                                    {
                                        byte* dest = (byte*)destPtr;

                                        for (int y = 0; y < height; y++)
                                        {
                                            byte* sourceRow = source + y * mappedResource.RowPitch;
                                            byte* destRow = dest + y * bytesPerRow;

                                            Buffer.MemoryCopy(sourceRow, destRow, bytesPerRow, bytesPerRow);
                                        }
                                    }
                                }

                                image = Image.LoadPixelData<Bgra32>(pixelData, width, height);
                            }

                            string filename = $"{DateTime.Now:yyyyMMdd_HHmmss_ff}_{adapterInfo.AdapterIndex}_{outputInfo.OutputIndex}.jpg";
                            string fullPath = Path.Combine(_screenshotsDir, filename);
                            JpegEncoder encoder = new() { Quality = 20 };
                            image.Save(fullPath, encoder);

                            screenshotCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex);
                        }
                        finally
                        {
                            image?.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex);
                    }
                    finally
                    {
                        if (stagingTexture != null) adapterInfo.Context.Unmap(stagingTexture, 0);
                        outputInfo.Duplication.ReleaseFrame();
                        stagingTexture?.Dispose();
                        texture2D?.Dispose();
                        desktopResource?.Dispose();
                    }

                }
            }
            if (screenshotCount == 0)
            {
                throw new Exception("No screenshots were captured.");
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Cleanup();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        private void Cleanup()
        {
            foreach (var adapterInfo in _duplicators)
            {
                foreach (var outputInfo in adapterInfo.Outputs)
                {
                    outputInfo.Duplication?.Dispose();
                    outputInfo.Output1?.Dispose();
                    outputInfo.Output?.Dispose();
                }
                adapterInfo.Context?.Dispose();
                adapterInfo.Device?.Dispose();
                adapterInfo.Adapter?.Dispose();
            }
            _duplicators.Clear();
            _factory?.Dispose();
        }
    }
}
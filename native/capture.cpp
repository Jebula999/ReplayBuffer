// capture.exe --list
//   prints one line per monitor: "index<TAB>width<TAB>height<TAB>name" (index = --monitor value)
// capture.exe --monitor N --height H --fps F --kbps K --start QPC [--pipe NAME] [--window a.exe;b.exe]
//   GPU-only screen recorder: Desktop Duplication -> D3D11 video processor (BGRA->NV12 + scale)
//   -> hardware H.264 encoder MFT. Writes H.264 in an IVF container (exact frame timestamps on the
//   shared QPC clock) to the named pipe, or to stdout without --pipe.
//   --window: while one of these programs runs, record only its window (Windows.Graphics.Capture),
//   so overlays, notifications and other windows stay out. Otherwise the monitor is recorded.
#define NOMINMAX
#include <windows.h>
#include <tlhelp32.h>
#include <dwmapi.h>
#include <atomic>
#include <string>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <d3d11.h>
#include <d3d10.h>
#include <dxgi1_2.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mftransform.h>
#include <mferror.h>
#include <strmif.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <vector>
#include <deque>
#include <mutex>
#include <condition_variable>
#include <thread>
#include <initguid.h>
#include <codecapi.h>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "winmm.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "dwmapi.lib")
#pragma comment(lib, "windowsapp.lib")

using Microsoft::WRL::ComPtr;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wd3d = winrt::Windows::Graphics::DirectX::Direct3D11;

static std::wstring lower(std::wstring s) {
    for (auto& c : s) c = towlower(c);
    return s;
}

static void die(const char* what, HRESULT hr) {
    fprintf(stderr, "capture: %s failed 0x%08lx\n", what, (unsigned long)hr);
    fflush(stderr);
    ExitProcess(1);
}
#define CHK(x) do { HRESULT _hr = (x); if (FAILED(_hr)) die(#x, _hr); } while (0)

static double now() {
    static LARGE_INTEGER f = {};
    if (!f.QuadPart) QueryPerformanceFrequency(&f);
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) / double(f.QuadPart);
}

static const int POOL = 8;

struct Capture {
    int monitor = 0, outH = 1080, outW = 1920, fps = 60, kbps = 20000;
    LONGLONG startQpc = 0;  // shared epoch with the audio writers (QueryPerformanceCounter ticks)

    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIOutput1> output;
    ComPtr<ID3D11Device> dev;
    ComPtr<ID3D11DeviceContext> ctx;
    ComPtr<ID3D11VideoDevice> vdev;
    ComPtr<ID3D11VideoContext> vctx;
    ComPtr<IDXGIOutputDuplication> dup;

    UINT srcW = 0, srcH = 0;
    ComPtr<ID3D11Texture2D> src;  // latest desktop frame (BGRA, native size)
    ComPtr<ID3D11VideoProcessorEnumerator> ven;
    ComPtr<ID3D11VideoProcessor> vp;
    ComPtr<ID3D11VideoProcessorInputView> inView;
    ComPtr<ID3D11Texture2D> pool[POOL];
    ComPtr<ID3D11VideoProcessorOutputView> outView[POOL];

    ComPtr<IMFTransform> mft;
    ComPtr<IMFMediaEventGenerator> events;
    ComPtr<IMFDXGIDeviceManager> mgr;
    bool providesSamples = true;
    DWORD outBufSize = 0;
    std::vector<BYTE> seqHeader;
    int needInput = 0;

    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);

    void pickOutput() {
        ComPtr<IDXGIFactory1> f;
        CHK(CreateDXGIFactory1(IID_PPV_ARGS(&f)));
        int n = 0;
        ComPtr<IDXGIAdapter1> a;
        for (UINT ai = 0; f->EnumAdapters1(ai, &a) != DXGI_ERROR_NOT_FOUND; ai++) {
            ComPtr<IDXGIOutput> o;
            for (UINT oi = 0; a->EnumOutputs(oi, &o) != DXGI_ERROR_NOT_FOUND; oi++) {
                if (n++ == monitor) {
                    adapter = a;
                    CHK(o.As(&output));
                    return;
                }
            }
        }
        die("find monitor", E_INVALIDARG);
    }

    void createDevice() {
        D3D_FEATURE_LEVEL fl;
        CHK(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                              D3D11_CREATE_DEVICE_VIDEO_SUPPORT | D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                              nullptr, 0, D3D11_SDK_VERSION, &dev, &fl, &ctx));
        ComPtr<ID3D10Multithread> mt;
        if (SUCCEEDED(ctx.As(&mt))) mt->SetMultithreadProtected(TRUE);
        CHK(dev.As(&vdev));
        CHK(ctx.As(&vctx));

        // ask for higher GPU scheduling priority so a GPU-bound game can't starve capture
        ComPtr<IDXGIDevice> dxgiDev;
        HRESULT tp = E_FAIL;
        if (SUCCEEDED(dev.As(&dxgiDev))) tp = dxgiDev->SetGPUThreadPriority(7);
        typedef LONG(WINAPI * SetPrio)(HANDLE, int);
        LONG cls = -1, st = -1;
        if (auto fn = (SetPrio)GetProcAddress(GetModuleHandleW(L"gdi32.dll"), "D3DKMTSetProcessSchedulingPriorityClass")) {
            if ((st = fn(GetCurrentProcess(), 5)) == 0) cls = 5;       // realtime (needs admin)
            else if ((st = fn(GetCurrentProcess(), 4)) == 0) cls = 4;  // high
        }
        fprintf(stderr, "capture: GPU priority %s (thread priority %s)\n",
                cls == 5 ? "realtime" : cls == 4 ? "high" : "normal - run as administrator for higher",
                SUCCEEDED(tp) ? "raised" : "unchanged");
        SetPriorityClass(GetCurrentProcess(), ABOVE_NORMAL_PRIORITY_CLASS);

        if (!windowExes.empty() && dxgiDev) {
            winrt::com_ptr<IInspectable> insp;
            if (SUCCEEDED(CreateDirect3D11DeviceFromDXGIDevice(dxgiDev.Get(), insp.put()))) wrtDev = insp.as<wd3d::IDirect3DDevice>();
            try {  // lets capture run without the yellow border (Windows 11)
                wgc::GraphicsCaptureAccess::RequestAccessAsync(wgc::GraphicsCaptureAccessKind::Borderless).get();
            } catch (...) {}
        }
    }

    // ---------------- game window capture ----------------

    std::vector<std::wstring> windowExes;  // lower-case exe names
    wd3d::IDirect3DDevice wrtDev{nullptr};
    wgc::GraphicsCaptureItem item{nullptr};
    wgc::Direct3D11CaptureFramePool framePool{nullptr};
    wgc::GraphicsCaptureSession session{nullptr};
    winrt::Windows::Graphics::SizeInt32 poolSize{};
    std::atomic<bool> itemClosed{false};
    HWND gameWnd = nullptr;
    bool windowMode = false;  // a listed game is running: record its window, never the desktop
    double nextFind = 0;

    bool exeMatches(const wchar_t* path) {
        const wchar_t* base = wcsrchr(path, L'\\');
        std::wstring n = lower(base ? base + 1 : path);
        for (auto& e : windowExes)
            if (e == n) return true;
        return false;
    }

    bool gameProcessRunning() {
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return false;
        PROCESSENTRY32W pe = {sizeof(pe)};
        bool found = false;
        for (BOOL ok = Process32FirstW(snap, &pe); ok && !found; ok = Process32NextW(snap, &pe)) found = exeMatches(pe.szExeFile);
        CloseHandle(snap);
        return found;
    }

    // the game's largest visible top-level window
    HWND findGameWindow() {
        struct Ctx {
            Capture* c;
            HWND best;
            LONGLONG area;
        } x = {this, nullptr, -1};
        EnumWindows([](HWND h, LPARAM lp) -> BOOL {
            auto& x = *(Ctx*)lp;
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) || (GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW)) return TRUE;
            BOOL cloaked = FALSE;
            DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));
            if (cloaked) return TRUE;
            DWORD pid = 0;
            GetWindowThreadProcessId(h, &pid);
            HANDLE p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (!p) return TRUE;
            wchar_t path[MAX_PATH];
            DWORD n = MAX_PATH;
            bool match = QueryFullProcessImageNameW(p, 0, path, &n) && x.c->exeMatches(path);
            CloseHandle(p);
            if (!match) return TRUE;
            RECT r;
            GetClientRect(h, &r);
            LONGLONG a = IsIconic(h) ? 0 : LONGLONG(r.right) * r.bottom;
            if (a > x.area) {
                x.area = a;
                x.best = h;
            }
            return TRUE;
        }, (LPARAM)&x);
        return x.best;
    }

    void stopWindowCapture() {
        if (session) session.Close();
        if (framePool) framePool.Close();
        session = nullptr;
        framePool = nullptr;
        item = nullptr;
        gameWnd = nullptr;
    }

    void startWindowCapture(HWND w) {
        gameWnd = w;
        wchar_t title[128] = L"";
        GetWindowTextW(w, title, 128);
        try {
            auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
            winrt::check_hresult(interop->CreateForWindow(w, winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(item)));
            poolSize = item.Size();
            framePool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
                wrtDev, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, poolSize);
            session = framePool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled(false); } catch (...) {}
            try { session.IsBorderRequired(false); } catch (...) {}
            item.Closed([this](auto&&, auto&&) { itemClosed = true; });
            session.StartCapture();
            fprintf(stderr, "capture: recording game window \"%ls\" (%dx%d)\n", title, poolSize.Width, poolSize.Height);
        } catch (winrt::hresult_error const& e) {
            fprintf(stderr, "capture: game window \"%ls\" can't be captured (0x%08lx)\n", title, (unsigned long)e.code().value);
            stopWindowCapture();
            gameWnd = w;  // don't retry this window
        }
        fflush(stderr);
    }

    void clearSource() {
        float black[4] = {0, 0, 0, 1};
        ComPtr<ID3D11RenderTargetView> rtv;
        if (SUCCEEDED(dev->CreateRenderTargetView(src.Get(), nullptr, &rtv))) ctx->ClearRenderTargetView(rtv.Get(), black);
    }

    // returns false when the desktop should be recorded instead
    bool grabWindow() {
        if (windowExes.empty() || !wrtDev) return false;
        double t = now();
        if (itemClosed.exchange(false)) {
            stopWindowCapture();
            nextFind = 0;
        }
        if (t >= nextFind) {  // re-check every 2 s: the game may swap its splash window for the real one
            nextFind = t + 2;
            HWND w = findGameWindow();
            if (w && w != gameWnd) {
                stopWindowCapture();
                startWindowCapture(w);
            }
            bool running = w || gameProcessRunning();
            if (running != windowMode) {
                windowMode = running;
                if (running) {
                    dup.Reset();  // stop desktop capture entirely
                    dupState = 0;
                    clearSource();
                    if (!w) fprintf(stderr, "capture: game running, waiting for its window\n");
                } else {
                    stopWindowCapture();
                    fprintf(stderr, "capture: game closed, recording the display\n");
                }
                fflush(stderr);
            }
        }
        if (!windowMode) return false;
        if (!session) return true;

        wgc::Direct3D11CaptureFrame frame{nullptr};
        try {
            while (auto f = framePool.TryGetNextFrame()) {  // keep only the newest
                if (frame) frame.Close();
                frame = f;
            }
        } catch (...) {
            return true;
        }
        if (!frame) return true;  // nothing new (or minimised): keep the last image
        auto size = frame.ContentSize();
        ComPtr<ID3D11Texture2D> tex;
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        if (SUCCEEDED(access->GetInterface(IID_PPV_ARGS(&tex)))) {
            D3D11_TEXTURE2D_DESC d;
            tex->GetDesc(&d);
            UINT w = std::min<UINT>(size.Width, d.Width), h = std::min<UINT>(size.Height, d.Height);
            if (w >= 16 && h >= 16) {
                if (w != srcW || h != srcH) createSourceChain(w, h);
                D3D11_BOX box = {0, 0, 0, w, h, 1};
                ctx->CopySubresourceRegion(src.Get(), 0, 0, 0, 0, tex.Get(), 0, &box);
            }
        }
        frame.Close();
        if (size.Width != poolSize.Width || size.Height != poolSize.Height) {  // window resized
            poolSize = size;
            framePool.Recreate(wrtDev, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        }
        return true;
    }

    int dupState = 0;  // 0 = not started, 1 = capturing, 2 = unavailable (logged once per change)
    double nextDupTry = 0;

    bool startDuplication() {
        dup.Reset();
        if (now() < nextDupTry) return false;
        HRESULT hr = output->DuplicateOutput(dev.Get(), &dup);
        if (FAILED(hr)) {
            nextDupTry = now() + 0.25;
            if (dupState != 2) {
                fprintf(stderr, "capture: screen capture unavailable (0x%08lx), retrying\n", (unsigned long)hr);
                fflush(stderr);
            }
            dupState = 2;
            return false;
        }
        if (dupState == 2) {
            fprintf(stderr, "capture: screen capture resumed\n");
            fflush(stderr);
        }
        dupState = 1;
        DXGI_OUTDUPL_DESC d;
        dup->GetDesc(&d);
        if (d.ModeDesc.Width != srcW || d.ModeDesc.Height != srcH) createSourceChain(d.ModeDesc.Width, d.ModeDesc.Height);
        return true;
    }

    void createSourceChain(UINT w, UINT h) {
        srcW = w;
        srcH = h;
        D3D11_TEXTURE2D_DESC td = {};
        td.Width = w;
        td.Height = h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        src.Reset();
        CHK(dev->CreateTexture2D(&td, nullptr, &src));

        D3D11_VIDEO_PROCESSOR_CONTENT_DESC cd = {};
        cd.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
        cd.InputFrameRate = {UINT(fps), 1};
        cd.InputWidth = w;
        cd.InputHeight = h;
        cd.OutputFrameRate = {UINT(fps), 1};
        cd.OutputWidth = outW;
        cd.OutputHeight = outH;
        cd.Usage = D3D11_VIDEO_USAGE_OPTIMAL_QUALITY;
        ven.Reset();
        vp.Reset();
        inView.Reset();
        CHK(vdev->CreateVideoProcessorEnumerator(&cd, &ven));
        CHK(vdev->CreateVideoProcessor(ven.Get(), 0, &vp));

        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC ivd = {};
        ivd.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        CHK(vdev->CreateVideoProcessorInputView(src.Get(), ven.Get(), &ivd, &inView));

        for (int i = 0; i < POOL; i++) {
            if (!pool[i]) {
                D3D11_TEXTURE2D_DESC nd = {};
                nd.Width = outW;
                nd.Height = outH;
                nd.MipLevels = 1;
                nd.ArraySize = 1;
                nd.Format = DXGI_FORMAT_NV12;
                nd.SampleDesc.Count = 1;
                nd.BindFlags = D3D11_BIND_RENDER_TARGET;
                CHK(dev->CreateTexture2D(&nd, nullptr, &pool[i]));
            }
            D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC ovd = {};
            ovd.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
            outView[i].Reset();
            CHK(vdev->CreateVideoProcessorOutputView(pool[i].Get(), ven.Get(), &ovd, &outView[i]));
        }

        // full-range RGB in -> BT.709 limited-range YUV out
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE in = {};
        in.RGB_Range = 0;
        D3D11_VIDEO_PROCESSOR_COLOR_SPACE oc = {};
        oc.RGB_Range = 1;
        oc.YCbCr_Matrix = 1;
        oc.Nominal_Range = D3D11_VIDEO_PROCESSOR_NOMINAL_RANGE_16_235;
        vctx->VideoProcessorSetStreamColorSpace(vp.Get(), 0, &in);
        vctx->VideoProcessorSetOutputColorSpace(vp.Get(), &oc);
        vctx->VideoProcessorSetStreamFrameFormat(vp.Get(), 0, D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        vctx->VideoProcessorSetStreamAutoProcessingMode(vp.Get(), 0, FALSE);
        // fit inside the output keeping the aspect ratio (black bars for odd-shaped windows)
        LONG fw = outW, fh = outH;
        if (LONGLONG(w) * outH > LONGLONG(h) * outW) fh = LONG(LONGLONG(outW) * h / w);
        else fw = LONG(LONGLONG(outH) * w / h);
        RECT sr = {0, 0, LONG(w), LONG(h)}, full = {0, 0, outW, outH};
        RECT dr = {(outW - fw) / 2, (outH - fh) / 2, (outW - fw) / 2 + fw, (outH - fh) / 2 + fh};
        D3D11_VIDEO_COLOR bg = {};
        bg.RGBA = {0, 0, 0, 1};
        vctx->VideoProcessorSetOutputBackgroundColor(vp.Get(), FALSE, &bg);
        vctx->VideoProcessorSetStreamSourceRect(vp.Get(), 0, TRUE, &sr);
        vctx->VideoProcessorSetStreamDestRect(vp.Get(), 0, TRUE, &dr);
        vctx->VideoProcessorSetOutputTargetRect(vp.Get(), TRUE, &full);
        fprintf(stderr, "capture: source %ux%u -> %dx%d @ %d fps\n", w, h, outW, outH, fps);
    }

    // copies the newest desktop image into src; keeps the previous one if nothing changed
    void grab() {
        if (grabWindow()) return;
        if (!dup && !startDuplication()) return;
        DXGI_OUTDUPL_FRAME_INFO info;
        ComPtr<IDXGIResource> res;
        HRESULT hr = dup->AcquireNextFrame(0, &info, &res);
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return;
        if (FAILED(hr)) {  // access lost: mode change, UAC, fullscreen switch
            if (hr != DXGI_ERROR_ACCESS_LOST) {
                fprintf(stderr, "capture: frame grab failed (0x%08lx), restarting capture\n", (unsigned long)hr);
                fflush(stderr);
            }
            dup.Reset();
            return;
        }
        if (info.LastPresentTime.QuadPart) {
            ComPtr<ID3D11Texture2D> tex;
            if (SUCCEEDED(res.As(&tex))) {
                D3D11_TEXTURE2D_DESC d;
                tex->GetDesc(&d);
                if (d.Width == srcW && d.Height == srcH) ctx->CopyResource(src.Get(), tex.Get());
            }
        }
        dup->ReleaseFrame();
    }

    // AMD (AMF), NVIDIA (NVENC) and Intel (Quick Sync) all ship a hardware H.264 encoder MFT. The one on the
    // capture GPU is tried first, then any other; the first that accepts our setup is used.
    void setupEncoder() {
        CHK(MFStartup(MF_VERSION));
        MFT_REGISTER_TYPE_INFO inT = {MFMediaType_Video, MFVideoFormat_NV12};
        MFT_REGISTER_TYPE_INFO outT = {MFMediaType_Video, MFVideoFormat_H264};
        IMFActivate** acts = nullptr;
        UINT32 count = 0;
        CHK(MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                      &inT, &outT, &acts, &count));
        if (!count) die("no hardware H.264 encoder (update your graphics driver)", E_FAIL);
        DXGI_ADAPTER_DESC1 ad;
        adapter->GetDesc1(&ad);
        std::vector<UINT32> order;
        for (UINT32 i = 0; i < count; i++) {
            UINT64 luid = 0;
            bool same = SUCCEEDED(acts[i]->GetUINT64(MFT_ENUM_ADAPTER_LUID, &luid)) && memcmp(&luid, &ad.AdapterLuid, sizeof(luid)) == 0;
            order.insert(same ? order.begin() : order.end(), i);
        }
        bool ok = false;
        for (UINT32 i : order) {
            WCHAR name[256] = L"?";
            acts[i]->GetString(MFT_FRIENDLY_NAME_Attribute, name, 256, nullptr);
            HRESULT hr = tryEncoder(acts[i]);
            if (SUCCEEDED(hr)) {
                fprintf(stderr, "capture: encoder %ls\n", name);
                ok = true;
                break;
            }
            fprintf(stderr, "capture: encoder %ls unusable (0x%08lx), trying the next\n", name, (unsigned long)hr);
            acts[i]->ShutdownObject();
            mft.Reset();
            events.Reset();
            mgr.Reset();
        }
        for (UINT32 i = 0; i < count; i++) acts[i]->Release();
        CoTaskMemFree(acts);
        fflush(stderr);
        if (!ok) die("no usable hardware H.264 encoder", E_FAIL);
    }

    HRESULT tryEncoder(IMFActivate* act) {
#define TRY(x) do { HRESULT _hr = (x); if (FAILED(_hr)) return _hr; } while (0)
        TRY(act->ActivateObject(IID_PPV_ARGS(&mft)));

        ComPtr<IMFAttributes> attrs;
        TRY(mft->GetAttributes(&attrs));
        attrs->SetUINT32(MF_TRANSFORM_ASYNC_UNLOCK, TRUE);

        UINT token;
        TRY(MFCreateDXGIDeviceManager(&token, &mgr));
        TRY(mgr->ResetDevice(dev.Get(), token));
        TRY(mft->ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, reinterpret_cast<ULONG_PTR>(mgr.Get())));

        ComPtr<ICodecAPI> codec;
        if (SUCCEEDED(mft.As(&codec))) {
            VARIANT v;
            v.vt = VT_UI4;
            v.ulVal = eAVEncCommonRateControlMode_CBR;
            codec->SetValue(&CODECAPI_AVEncCommonRateControlMode, &v);
            v.ulVal = kbps * 1000;
            codec->SetValue(&CODECAPI_AVEncCommonMeanBitRate, &v);
            v.ulVal = fps * 2;  // keyframe every 2 s = buffer segment length
            codec->SetValue(&CODECAPI_AVEncMPVGOPSize, &v);
            v.ulVal = 0;
            codec->SetValue(&CODECAPI_AVEncMPVDefaultBPictureCount, &v);
            // no low-latency mode: lets the encoder queue several frames, so a GPU-bound game can't stall it
        }

        ComPtr<IMFMediaType> ot;
        CHK(MFCreateMediaType(&ot));
        ot->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        ot->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264);
        ot->SetUINT32(MF_MT_AVG_BITRATE, kbps * 1000);
        MFSetAttributeSize(ot.Get(), MF_MT_FRAME_SIZE, outW, outH);
        MFSetAttributeRatio(ot.Get(), MF_MT_FRAME_RATE, fps, 1);
        MFSetAttributeRatio(ot.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        ot->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        ot->SetUINT32(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_High);
        ot->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709);
        ot->SetUINT32(MF_MT_TRANSFER_FUNCTION, MFVideoTransFunc_709);
        ot->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
        ot->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
        TRY(mft->SetOutputType(0, ot.Get(), 0));

        ComPtr<IMFMediaType> it;
        CHK(MFCreateMediaType(&it));
        it->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        it->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        MFSetAttributeSize(it.Get(), MF_MT_FRAME_SIZE, outW, outH);
        MFSetAttributeRatio(it.Get(), MF_MT_FRAME_RATE, fps, 1);
        MFSetAttributeRatio(it.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        it->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        it->SetUINT32(MF_MT_VIDEO_PRIMARIES, MFVideoPrimaries_BT709);
        it->SetUINT32(MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
        it->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
        TRY(mft->SetInputType(0, it.Get(), 0));

        MFT_OUTPUT_STREAM_INFO si = {};
        TRY(mft->GetOutputStreamInfo(0, &si));
        providesSamples = (si.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
        outBufSize = si.cbSize ? si.cbSize : outW * outH * 2;

        TRY(mft.As(&events));
        TRY(mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0));
        TRY(mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0));
        return S_OK;
#undef TRY
    }

    // IVF container: 32-byte file header, then per frame [u32 size][u64 pts] + data.
    // Carries exact frame timestamps (pts = frame index since the shared epoch, timebase 1/fps).
    void writeIvfHeader() {
        BYTE h[32] = {'D', 'K', 'I', 'F', 0, 0, 32, 0, 'H', '2', '6', '4'};
        *(UINT16*)(h + 12) = UINT16(outW);
        *(UINT16*)(h + 14) = UINT16(outH);
        *(UINT32*)(h + 16) = UINT32(fps);  // timebase denominator
        *(UINT32*)(h + 20) = 1;            // timebase numerator
        write(h, 32);
    }

    void writeIvfFrame(const BYTE* pre, DWORD preLen, const BYTE* p, DWORD n, LONGLONG pts) {
        BYTE h[12];
        *(UINT32*)h = preLen + n;
        *(UINT64*)(h + 4) = UINT64(pts);
        write(h, 12);
        if (preLen) write(pre, preLen);
        write(p, n);
    }

    // Output goes through a queue drained by its own thread, so the capture loop never waits on the reader.
    std::mutex qm;
    std::condition_variable qcv;
    std::deque<std::vector<BYTE>> queue;
    size_t queued = 0;

    void write(const BYTE* p, DWORD n) {
        std::lock_guard<std::mutex> lock(qm);
        if (queued > (256u << 20)) die("output queue overflow (reader stopped)", E_FAIL);
        queue.emplace_back(p, p + n);
        queued += n;
        qcv.notify_one();
    }

    void writerLoop() {
        for (;;) {
            std::vector<BYTE> b;
            {
                std::unique_lock<std::mutex> lock(qm);
                qcv.wait(lock, [&] { return !queue.empty(); });
                b = std::move(queue.front());
                queue.pop_front();
                queued -= b.size();
            }
            const BYTE* p = b.data();
            DWORD n = DWORD(b.size()), w = 0;
            while (n) {
                if (!WriteFile(out, p, n, &w, nullptr)) ExitProcess(0);  // reader gone
                p += w;
                n -= w;
            }
        }
    }

    void handleOutput() {
        MFT_OUTPUT_DATA_BUFFER ob = {};
        ComPtr<IMFSample> own;
        if (!providesSamples) {
            ComPtr<IMFMediaBuffer> b;
            CHK(MFCreateMemoryBuffer(outBufSize, &b));
            CHK(MFCreateSample(&own));
            own->AddBuffer(b.Get());
            ob.pSample = own.Get();
        }
        DWORD status = 0;
        HRESULT hr = mft->ProcessOutput(0, 1, &ob, &status);
        if (ob.pEvents) ob.pEvents->Release();
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
            ComPtr<IMFMediaType> t;
            if (SUCCEEDED(mft->GetOutputAvailableType(0, 0, &t))) mft->SetOutputType(0, t.Get(), 0);
            return;
        }
        if (FAILED(hr)) {
            if (ob.pSample && providesSamples) ob.pSample->Release();
            return;
        }
        if (seqHeader.empty()) {
            ComPtr<IMFMediaType> cur;
            UINT32 sz = 0;
            if (SUCCEEDED(mft->GetOutputCurrentType(0, &cur)) && SUCCEEDED(cur->GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, &sz)) && sz) {
                seqHeader.resize(sz);
                cur->GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, seqHeader.data(), sz, nullptr);
            }
        }
        ComPtr<IMFMediaBuffer> buf;
        if (SUCCEEDED(ob.pSample->ConvertToContiguousBuffer(&buf))) {
            BYTE* p;
            DWORD len;
            if (SUCCEEDED(buf->Lock(&p, nullptr, &len))) {
                UINT32 key = 0;
                ob.pSample->GetUINT32(MFSampleExtension_CleanPoint, &key);
                LONGLONG t = 0;
                ob.pSample->GetSampleTime(&t);
                bool addHeader = key && !seqHeader.empty() && !hasSps(p, len);
                writeIvfFrame(addHeader ? seqHeader.data() : nullptr, addHeader ? DWORD(seqHeader.size()) : 0,
                              p, len, (t * fps + 5000000) / 10000000);
                buf->Unlock();
            }
        }
        if (providesSamples) ob.pSample->Release();
    }

    static bool hasSps(const BYTE* p, DWORD n) {
        for (DWORD i = 0; i + 4 < n && i < 256; i++)
            if (p[i] == 0 && p[i + 1] == 0 && p[i + 2] == 1 && (p[i + 3] & 0x1f) == 7) return true;
        return false;
    }

    // returns true if an event was handled
    bool pump(bool wait) {
        ComPtr<IMFMediaEvent> ev;
        HRESULT hr = events->GetEvent(wait ? 0 : MF_EVENT_FLAG_NO_WAIT, &ev);
        if (FAILED(hr)) return false;
        MediaEventType type;
        ev->GetType(&type);
        if (type == METransformNeedInput) needInput++;
        else if (type == METransformHaveOutput) handleOutput();
        return true;
    }

    double* waitStat = nullptr;
    void encode(int slot, LONGLONG tick) {
        double w0 = now();
        while (needInput == 0) pump(true);
        if (waitStat) *waitStat += now() - w0;
        ComPtr<IMFMediaBuffer> buf;
        CHK(MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), pool[slot].Get(), 0, FALSE, &buf));
        ComPtr<IMF2DBuffer> b2;
        DWORD len = 0;
        if (SUCCEEDED(buf.As(&b2)) && SUCCEEDED(b2->GetContiguousLength(&len))) buf->SetCurrentLength(len);
        ComPtr<IMFSample> s;
        CHK(MFCreateSample(&s));
        s->AddBuffer(buf.Get());
        s->SetSampleTime(tick * 10000000LL / fps);
        s->SetSampleDuration(10000000LL / fps);
        HRESULT hr = mft->ProcessInput(0, s.Get(), 0);
        if (FAILED(hr)) die("ProcessInput", hr);
        needInput--;
    }

    void run() {
        pickOutput();
        createDevice();
        DXGI_OUTPUT_DESC od;
        output->GetDesc(&od);
        UINT nw = od.DesktopCoordinates.right - od.DesktopCoordinates.left;
        UINT nh = od.DesktopCoordinates.bottom - od.DesktopCoordinates.top;
        if (outH <= 0 || outH > int(nh)) outH = nh;
        outW = int((double(nw) * outH / nh) + 1) & ~1;
        outH &= ~1;
        createSourceChain(nw, nh);
        clearSource();
        if (!grabWindow()) startDuplication();
        setupEncoder();
        std::thread(&Capture::writerLoop, this).detach();
        writeIvfHeader();

        timeBeginPeriod(1);
        HANDLE timer = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        const double period = 1.0 / fps;
        // tick N always belongs at epoch + N/fps. Each frame is stamped with the tick of the moment it was
        // captured; if the GPU is too busy to keep up, ticks are skipped (dropped frames) instead of
        // stretching time, so playback speed and audio sync stay correct.
        LARGE_INTEGER qf;
        QueryPerformanceFrequency(&qf);
        double t0 = startQpc ? double(startQpc) / double(qf.QuadPart) : now();
        LONGLONG tick = 0, frame = 0, skipped = 0;
        double statT = now(), waitEnc = 0;
        LONGLONG statF = 0, statS = 0;
        waitStat = &waitEnc;
        for (;;) {
            double wait = t0 + tick * period - now();
            if (wait > 0.0005) {
                LARGE_INTEGER due;
                due.QuadPart = -LONGLONG(wait * 1e7);
                SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE);
                WaitForSingleObject(timer, INFINITE);
            } else if (-wait >= period) {
                LONGLONG cur = LONGLONG((now() - t0) / period);
                skipped += cur - tick;
                tick = cur;
            }
            grab();
            int slot = int(frame % POOL);
            D3D11_VIDEO_PROCESSOR_STREAM st = {};
            st.Enable = TRUE;
            st.pInputSurface = inView.Get();
            vctx->VideoProcessorBlt(vp.Get(), outView[slot].Get(), 0, 1, &st);
            encode(slot, tick);
            frame++;
            tick++;
            while (pump(false)) {}
            if (now() - statT >= 5) {
                // report straight away when frames drop, otherwise once a minute
                double secs = now() - statT;
                static int quiet = 0;
                if (skipped > statS || ++quiet >= 12) {
                    quiet = 0;
                    fprintf(stderr, "capture: %.1f fps encoded, %.1f fps dropped (fell behind), %.0f%% waiting on encoder\n",
                            (frame - statF) / secs, (skipped - statS) / secs, 100 * waitEnc / secs);
                    fflush(stderr);
                }
                statT = now();
                statF = frame;
                statS = skipped;
                waitEnc = 0;
            }
        }
    }
};

static int listMonitors() {
    ComPtr<IDXGIFactory1> f;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&f)))) return 1;
    int n = 0;
    ComPtr<IDXGIAdapter1> a;
    for (UINT ai = 0; f->EnumAdapters1(ai, &a) != DXGI_ERROR_NOT_FOUND; ai++) {
        ComPtr<IDXGIOutput> o;
        for (UINT oi = 0; a->EnumOutputs(oi, &o) != DXGI_ERROR_NOT_FOUND; oi++) {
            DXGI_OUTPUT_DESC d;
            o->GetDesc(&d);
            printf("%d\t%ld\t%ld\t%ls\n", n++, d.DesktopCoordinates.right - d.DesktopCoordinates.left,
                   d.DesktopCoordinates.bottom - d.DesktopCoordinates.top, d.DeviceName);
        }
    }
    fflush(stdout);
    return 0;
}

int main(int argc, char** argv) {
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);  // real pixel sizes
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (argc > 1 && !strcmp(argv[1], "--list")) return listMonitors();
    Capture c;
    const char* pipeName = nullptr;
    for (int i = 1; i + 1 < argc; i += 2) {
        if (!strcmp(argv[i], "--pipe")) pipeName = argv[i + 1];
        else if (!strcmp(argv[i], "--monitor")) c.monitor = atoi(argv[i + 1]);
        else if (!strcmp(argv[i], "--height")) c.outH = atoi(argv[i + 1]);
        else if (!strcmp(argv[i], "--fps")) c.fps = atoi(argv[i + 1]);
        else if (!strcmp(argv[i], "--kbps")) c.kbps = atoi(argv[i + 1]);
        else if (!strcmp(argv[i], "--start")) c.startQpc = _strtoi64(argv[i + 1], nullptr, 10);
        else if (!strcmp(argv[i], "--window")) {
            for (char* tok = strtok(argv[i + 1], ";"); tok; tok = strtok(nullptr, ";")) {
                std::wstring w(tok, tok + strlen(tok));
                if (!w.empty()) c.windowExes.push_back(lower(w));
            }
        }
    }
    if (pipeName) {
        // serve the stream on a named pipe; "ready" tells the app it can start ffmpeg
        c.out = CreateNamedPipeA(pipeName, PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 1 << 20, 1 << 20, 0, nullptr);
        if (c.out == INVALID_HANDLE_VALUE) die("CreateNamedPipe", HRESULT_FROM_WIN32(GetLastError()));
        printf("ready\n");
        fflush(stdout);
        if (!ConnectNamedPipe(c.out, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED)
            die("ConnectNamedPipe", HRESULT_FROM_WIN32(GetLastError()));
    }
    c.run();
    return 0;
}




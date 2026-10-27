// audio.exe --list
//   prints one line per active device: "out|in<TAB>endpoint-id<TAB>friendly name" (UTF-8)
// audio.exe --pipe NAME --epoch QPC (--device ID [--loopback] | --apps a.exe;b.exe)
//   captures one source as 48 kHz stereo s16le and streams it into a named pipe for ffmpeg.
//   Sample N of the stream belongs at epoch + N/48000 (the same clock capture.exe uses), with gaps
//   filled by silence, so every track lines up with the video no matter when it starts or stalls.
#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <tlhelp32.h>
#include <wrl/client.h>
#include <cstdio>
#include <string>
#include <vector>
#include <mutex>
#include <thread>
#include <atomic>

#pragma comment(lib, "mmdevapi.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "winmm.lib")

using Microsoft::WRL::ComPtr;

static const int RATE = 48000, CH = 2, FB = 4;  // frame bytes
static const double LATENCY = 0.04;            // stream time trails real time by this (jitter buffer)

static double qpcSeconds() {
    static LARGE_INTEGER f = {};
    if (!f.QuadPart) QueryPerformanceFrequency(&f);
    LARGE_INTEGER c;
    QueryPerformanceCounter(&c);
    return double(c.QuadPart) / double(f.QuadPart);
}

static std::string utf8(const wchar_t* w) {
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}

static std::wstring wide(const char* s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    std::wstring w(n > 0 ? n - 1 : 0, L'\0');
    if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s, -1, w.data(), n);
    return w;
}

static WAVEFORMATEX pcmFormat() {
    WAVEFORMATEX f = {};
    f.wFormatTag = WAVE_FORMAT_PCM;
    f.nChannels = CH;
    f.nSamplesPerSec = RATE;
    f.wBitsPerSample = 16;
    f.nBlockAlign = FB;
    f.nAvgBytesPerSec = RATE * FB;
    return f;
}

// ---------------- shared buffer between capture and writer ----------------

static std::mutex bufLock;
static std::vector<BYTE> buf;
static std::atomic<bool> connected{false};

static void push(const BYTE* p, size_t n) {
    std::lock_guard<std::mutex> l(bufLock);
    buf.insert(buf.end(), p, p + n);
    size_t cap = size_t(RATE * (connected ? 2 * LATENCY + 0.05 : 0.2)) * FB;
    if (buf.size() > cap) {  // device clock running fast, or ffmpeg not reading yet: drop oldest
        size_t drop = buf.size() - size_t(RATE * LATENCY) * FB;
        drop -= drop % FB;
        buf.erase(buf.begin(), buf.begin() + drop);
    }
}

// ---------------- capture from an IAudioClient ----------------

static bool pump(IAudioClient* client, IAudioCaptureClient* cap, HANDLE stopWhen) {
    std::vector<BYTE> zeros;
    for (;;) {
        if (stopWhen && WaitForSingleObject(stopWhen, 0) == WAIT_OBJECT_0) return true;
        UINT32 packet = 0;
        HRESULT hr = cap->GetNextPacketSize(&packet);
        if (FAILED(hr)) return false;  // device removed / invalidated
        while (packet) {
            BYTE* data;
            UINT32 frames;
            DWORD flags;
            if (FAILED(cap->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) return false;
            if (flags & AUDCLNT_BUFFERFLAGS_SILENT) {
                zeros.assign(size_t(frames) * FB, 0);
                push(zeros.data(), zeros.size());
            } else {
                push(data, size_t(frames) * FB);
            }
            cap->ReleaseBuffer(frames);
            if (FAILED(cap->GetNextPacketSize(&packet))) return false;
        }
        Sleep(5);
    }
}

static void deviceLoop(std::wstring id, bool loopback) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    for (;;) {
        ComPtr<IMMDeviceEnumerator> en;
        ComPtr<IMMDevice> dev;
        ComPtr<IAudioClient> client;
        ComPtr<IAudioCaptureClient> cap;
        WAVEFORMATEX f = pcmFormat();
        if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en))) &&
            SUCCEEDED(en->GetDevice(id.c_str(), &dev)) &&
            SUCCEEDED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, &client)) &&
            SUCCEEDED(client->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                         (loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0) |
                                             AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                         2000000, 0, &f, nullptr)) &&
            SUCCEEDED(client->GetService(IID_PPV_ARGS(&cap))) && SUCCEEDED(client->Start())) {
            pump(client.Get(), cap.Get(), nullptr);
            client->Stop();
        }
        Sleep(2000);  // device unplugged or busy: retry
    }
}

// ---------------- per-application capture (process loopback, Windows 10 2004+) ----------------

class Handler : public IActivateAudioInterfaceCompletionHandler, public IAgileObject {
public:
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override {
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IActivateAudioInterfaceCompletionHandler))
            *ppv = static_cast<IActivateAudioInterfaceCompletionHandler*>(this);
        else if (riid == __uuidof(IAgileObject))
            *ppv = static_cast<IAgileObject*>(this);
        else {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP ActivateCompleted(IActivateAudioInterfaceAsyncOperation*) override {
        SetEvent(done);
        return S_OK;
    }
};

// root process of the first running app in the list (so its whole process tree is captured)
static DWORD findApp(const std::vector<std::wstring>& names) {
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    std::vector<PROCESSENTRY32W> all;
    PROCESSENTRY32W pe = {sizeof(pe)};
    for (BOOL ok = Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe)) all.push_back(pe);
    CloseHandle(snap);
    for (auto& n : names) {
        for (auto& p : all) {
            if (_wcsicmp(p.szExeFile, n.c_str())) continue;
            bool parentSame = false;
            for (auto& q : all)
                if (q.th32ProcessID == p.th32ParentProcessID && !_wcsicmp(q.szExeFile, p.szExeFile)) parentSame = true;
            if (!parentSame) return p.th32ProcessID;
        }
    }
    return 0;
}

static void appLoop(std::vector<std::wstring> names) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    for (;;) {
        DWORD pid = findApp(names);
        HANDLE proc = pid ? OpenProcess(SYNCHRONIZE, FALSE, pid) : nullptr;
        if (proc) {
            AUDIOCLIENT_ACTIVATION_PARAMS params = {};
            params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
            params.ProcessLoopbackParams.TargetProcessId = pid;
            params.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
            PROPVARIANT pv = {};
            pv.vt = VT_BLOB;
            pv.blob.cbSize = sizeof(params);
            pv.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
            Handler h;
            ComPtr<IActivateAudioInterfaceAsyncOperation> op;
            if (SUCCEEDED(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &pv, &h, &op))) {
                WaitForSingleObject(h.done, 5000);
                HRESULT act = E_FAIL;
                ComPtr<IUnknown> unk;
                op->GetActivateResult(&act, &unk);
                ComPtr<IAudioClient> client;
                ComPtr<IAudioCaptureClient> cap;
                WAVEFORMATEX f = pcmFormat();
                if (SUCCEEDED(act) && SUCCEEDED(unk.As(&client)) &&
                    SUCCEEDED(client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, &f, nullptr)) &&
                    SUCCEEDED(client->GetService(IID_PPV_ARGS(&cap))) && SUCCEEDED(client->Start())) {
                    pump(client.Get(), cap.Get(), proc);  // returns when the app exits
                    client->Stop();
                }
            }
            CloseHandle(proc);
        }
        Sleep(2000);  // app not running (yet): look again
    }
}

// ---------------- device list ----------------

static int listDevices() {
    SetConsoleOutputCP(CP_UTF8);
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ComPtr<IMMDeviceEnumerator> en;
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&en)))) return 1;
    for (EDataFlow flow : {eRender, eCapture}) {
        ComPtr<IMMDeviceCollection> col;
        if (FAILED(en->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &col))) continue;
        UINT n = 0;
        col->GetCount(&n);
        for (UINT i = 0; i < n; i++) {
            ComPtr<IMMDevice> d;
            ComPtr<IPropertyStore> props;
            LPWSTR id = nullptr;
            PROPVARIANT name;
            PropVariantInit(&name);
            if (FAILED(col->Item(i, &d)) || FAILED(d->GetId(&id))) continue;
            if (SUCCEEDED(d->OpenPropertyStore(STGM_READ, &props))) props->GetValue(PKEY_Device_FriendlyName, &name);
            printf("%s\t%s\t%s\n", flow == eRender ? "out" : "in", utf8(id).c_str(),
                   name.vt == VT_LPWSTR ? utf8(name.pwszVal).c_str() : "Unknown device");
            PropVariantClear(&name);
            CoTaskMemFree(id);
        }
    }
    fflush(stdout);
    return 0;
}

// ---------------- main: pipe writer on the shared clock ----------------

int main(int argc, char** argv) {
    std::wstring pipeName, device, apps;
    bool loopback = false;
    LONGLONG epochQpc = 0;
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        if (a == "--list") return listDevices();
        if (a == "--loopback") loopback = true;
        else if (i + 1 < argc) {
            if (a == "--pipe") pipeName = wide(argv[++i]);
            else if (a == "--device") device = wide(argv[++i]);
            else if (a == "--apps") apps = wide(argv[++i]);
            else if (a == "--epoch") epochQpc = _strtoi64(argv[++i], nullptr, 10);
        }
    }
    if (pipeName.empty() || (device.empty() && apps.empty())) {
        fprintf(stderr, "usage: audio --list | --pipe NAME --epoch QPC (--device ID [--loopback] | --apps a.exe;b.exe)\n");
        return 2;
    }
    LARGE_INTEGER qf;
    QueryPerformanceFrequency(&qf);
    double epoch = epochQpc ? double(epochQpc) / double(qf.QuadPart) : qpcSeconds();

    HANDLE pipe = CreateNamedPipeW(pipeName.c_str(), PIPE_ACCESS_OUTBOUND, PIPE_TYPE_BYTE | PIPE_WAIT, 1, 65536, 65536, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
        fprintf(stderr, "audio: CreateNamedPipe failed %lu\n", GetLastError());
        return 1;
    }
    printf("ready\n");
    fflush(stdout);

    if (!device.empty()) {
        std::thread(deviceLoop, device, loopback).detach();
    } else {
        std::vector<std::wstring> names;
        size_t s = 0;
        while (s <= apps.size()) {
            size_t e = apps.find(L';', s);
            if (e == std::wstring::npos) e = apps.size();
            if (e > s) names.push_back(apps.substr(s, e - s));
            s = e + 1;
        }
        std::thread(appLoop, names).detach();
    }

    if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED) return 1;
    connected = true;

    timeBeginPeriod(1);
    LONGLONG sent = 0;  // samples written = stream time; never reset
    std::vector<BYTE> chunk;
    for (;;) {
        LONGLONG due = LONGLONG((qpcSeconds() - epoch - LATENCY) * RATE) - sent;
        if (due <= 0) {
            Sleep(5);
            continue;
        }
        size_t n = size_t(due) * FB;
        chunk.assign(n, 0);
        {
            std::lock_guard<std::mutex> l(bufLock);
            size_t have = buf.size() < n ? buf.size() : n;
            // missing audio (silence / catch-up) goes before the newest samples
            memcpy(chunk.data() + (n - have), buf.data(), have);
            buf.erase(buf.begin(), buf.begin() + have);
        }
        const BYTE* p = chunk.data();
        DWORD left = DWORD(n), w = 0;
        while (left) {
            if (!WriteFile(pipe, p, left, &w, nullptr)) return 0;  // ffmpeg gone
            p += w;
            left -= w;
        }
        sent += due;
    }
}

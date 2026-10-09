#include "common.h"
#include <propvarutil.h>

namespace hr {
namespace {
struct MonitorContext { hr_device_callback callback; void* user; int index; };
BOOL CALLBACK monitor_callback(HMONITOR monitor, HDC, LPRECT, LPARAM parameter) {
    auto* context = reinterpret_cast<MonitorContext*>(parameter);
    MONITORINFOEXW info{}; info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return TRUE;
    // info.szDevice (\\.\DISPLAYn) is the stable ID kept in settings and capture config.
    // The caption is for people: ordinal in this enumeration, size, and whether it is primary.
    const bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
    const auto width = info.rcMonitor.right - info.rcMonitor.left;
    const auto height = info.rcMonitor.bottom - info.rcMonitor.top;
    std::wstring label = L"Монитор " + std::to_wstring(++context->index) + L" · " + std::to_wstring(width) +
        L" × " + std::to_wstring(height) + (primary ? L" · основной" : L"");
    hr_device item{0, primary, 0, reinterpret_cast<uint64_t>(monitor), info.szDevice, label.c_str()};
    context->callback(context->user, &item);
    return TRUE;
}
void endpoints(IMMDeviceEnumerator* enumerator, EDataFlow flow, int kind, hr_device_callback callback, void* user) {
    ComPtr<IMMDevice> default_device;
    std::wstring default_id;
    if (SUCCEEDED(enumerator->GetDefaultAudioEndpoint(flow, eConsole, &default_device))) {
        LPWSTR id = nullptr;
        if (SUCCEEDED(default_device->GetId(&id))) { default_id = id; CoTaskMemFree(id); }
    }
    ComPtr<IMMDeviceCollection> collection;
    check(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &collection));
    UINT count = 0; check(collection->GetCount(&count));
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> device; check(collection->Item(i, &device));
        LPWSTR id = nullptr; check(device->GetId(&id));
        std::wstring identifier(id); CoTaskMemFree(id);
        ComPtr<IPropertyStore> properties; check(device->OpenPropertyStore(STGM_READ, &properties));
        PROPVARIANT value; PropVariantInit(&value);
        check(properties->GetValue(PKEY_Device_FriendlyName, &value));
        std::wstring label = value.vt == VT_LPWSTR ? value.pwszVal : identifier;
        PropVariantClear(&value);
        hr_device item{kind, identifier == default_id, 0, 0, identifier.c_str(), label.c_str()};
        callback(user, &item);
    }
}
}
void enumerate(hr_device_callback callback, void* user) {
    Apartment apartment;
    MonitorContext context{callback, user, 0};
    if (!EnumDisplayMonitors(nullptr, nullptr, monitor_callback, reinterpret_cast<LPARAM>(&context)))
        check(HRESULT_FROM_WIN32(GetLastError()));
    ComPtr<IDXGIFactory1> factory; check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)));
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> adapter;
        HRESULT enumerated = factory->EnumAdapters1(i, &adapter);
        if (enumerated == DXGI_ERROR_NOT_FOUND) break;
        check(enumerated);
        DXGI_ADAPTER_DESC1 desc{}; check(adapter->GetDesc1(&desc));
        if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        auto id = std::to_wstring(i);
        hr_device item{3, i == 0, static_cast<int32_t>(desc.VendorId), 0, id.c_str(), desc.Description};
        callback(user, &item);
    }
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)));
    endpoints(enumerator.Get(), eRender, 1, callback, user);
    endpoints(enumerator.Get(), eCapture, 2, callback, user);
}
}

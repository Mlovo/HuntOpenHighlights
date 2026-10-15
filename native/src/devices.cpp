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
    // The label is a language-neutral fallback; the app builds the localized caption from the handle.
    const bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
    const auto width = info.rcMonitor.right - info.rcMonitor.left;
    const auto height = info.rcMonitor.bottom - info.rcMonitor.top;
    std::wstring label = L"Monitor " + std::to_wstring(++context->index) + L" · " + std::to_wstring(width) +
        L" × " + std::to_wstring(height) + (primary ? L" · primary" : L"");
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
    struct AdapterEntry {
        UINT index;
        DXGI_ADAPTER_DESC1 desc;
        UINT output_count;
    };
    std::vector<AdapterEntry> adapters;
    for (UINT i = 0; ; ++i) {
        ComPtr<IDXGIAdapter1> adapter;
        HRESULT enumerated = factory->EnumAdapters1(i, &adapter);
        if (enumerated == DXGI_ERROR_NOT_FOUND) break;
        check(enumerated);
        DXGI_ADAPTER_DESC1 desc{}; check(adapter->GetDesc1(&desc));
        if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
        UINT outputs = 0;
        for (UINT j = 0; ; ++j) {
            ComPtr<IDXGIOutput> output;
            if (adapter->EnumOutputs(j, &output) == DXGI_ERROR_NOT_FOUND) break;
            ++outputs;
        }
        adapters.push_back({i, desc, outputs});
    }
    std::vector<AdapterEntry> filtered;
    for (const auto& a : adapters) {
        bool is_headless_duplicate = false;
        for (const auto& other : adapters) {
            if (other.index != a.index &&
                other.desc.AdapterLuid.LowPart == a.desc.AdapterLuid.LowPart &&
                other.desc.AdapterLuid.HighPart == a.desc.AdapterLuid.HighPart &&
                other.output_count > 0 && a.output_count == 0) {
                is_headless_duplicate = true;
                break;
            }
        }
        if (!is_headless_duplicate) filtered.push_back(a);
    }
    bool first_hardware = true;
    for (size_t k = 0; k < filtered.size(); ++k) {
        const auto& a = filtered[k];
        size_t same_name_count = 0;
        size_t rank = 0;
        for (size_t m = 0; m < filtered.size(); ++m) {
            if (wcscmp(filtered[m].desc.Description, a.desc.Description) == 0) {
                ++same_name_count;
                if (m <= k) ++rank;
            }
        }
        std::wstring label = a.desc.Description;
        if (same_name_count > 1) {
            label += L" · #" + std::to_wstring(rank);
        }
        // Adapter order shifts with display topology; persist the name unless it is ambiguous.
        auto id = same_name_count > 1 ? std::to_wstring(a.index) : std::wstring(a.desc.Description);
        hr_device item{3, first_hardware ? 1 : 0, static_cast<int32_t>(a.desc.VendorId), 0, id.c_str(), label.c_str()};
        first_hardware = false;
        callback(user, &item);
    }
    ComPtr<IMMDeviceEnumerator> enumerator;
    check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator)));
    endpoints(enumerator.Get(), eRender, 1, callback, user);
    endpoints(enumerator.Get(), eCapture, 2, callback, user);
}
}

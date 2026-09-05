#include "rendering/ShellIconLoader.h"

#include <ShlObj.h>
#include <shellapi.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <deque>
#include <iterator>
#include <map>
#include <mutex>
#include <optional>
#include <thread>
#include <type_traits>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace
{
	constexpr size_t MaximumPendingIcons = 128;
	constexpr size_t MaximumCachedIcons = 192;
	constexpr size_t MaximumCompletedIcons = 128;
	constexpr UINT MinimumIconPixels = 8;
	constexpr UINT MaximumIconPixels = 256;
	constexpr DWORD WorkerShutdownTimeoutMs = 1500;
	constexpr auto SuccessfulCacheLifetime = std::chrono::minutes(10);
	constexpr auto FailedCacheLifetime = std::chrono::seconds(30);

	struct IconKey final
	{
		ArkheideSystem::ShellIconSourceKind sourceKind{};
		std::wstring source;
		UINT pixelSize = 0;
	};

	struct IconKeyLess final
	{
		bool operator()(const IconKey& left, const IconKey& right) const noexcept
		{
			if (left.sourceKind != right.sourceKind)
			{
				return left.sourceKind < right.sourceKind;
			}
			if (left.pixelSize != right.pixelSize)
			{
				return left.pixelSize < right.pixelSize;
			}
			const auto comparison = CompareStringOrdinal(
				left.source.c_str(),
				static_cast<int>(left.source.size()),
				right.source.c_str(),
				static_cast<int>(right.source.size()),
				TRUE);
			if (comparison == CSTR_LESS_THAN) return true;
			if (comparison == CSTR_GREATER_THAN) return false;
			return false;
		}
	};

	struct Subscriber final
	{
		uint64_t token = 0;
		uint64_t generation = 0;
	};

	bool EquivalentIconKey(const IconKey& left, const IconKey& right) noexcept
	{
		const IconKeyLess less;
		return !less(left, right) && !less(right, left);
	}

	struct IconHandleDeleter final
	{
		void operator()(std::remove_pointer_t<HICON>* handle) const noexcept
		{
			if (handle != nullptr) DestroyIcon(handle);
		}
	};

	struct BitmapHandleDeleter final
	{
		void operator()(std::remove_pointer_t<HBITMAP>* handle) const noexcept
		{
			if (handle != nullptr) DeleteObject(handle);
		}
	};

	using UniqueIconHandle = std::unique_ptr<std::remove_pointer_t<HICON>, IconHandleDeleter>;
	using UniqueBitmapHandle = std::unique_ptr<std::remove_pointer_t<HBITMAP>, BitmapHandleDeleter>;

	struct CacheEntry final
	{
		std::shared_ptr<const ArkheideSystem::ShellIconBitmapData> bitmap;
		std::chrono::steady_clock::time_point expiresAt{};
		uint64_t lastAccess = 0;
	};

	HRESULT ConvertBitmapSource(
		IWICImagingFactory* factory,
		IWICBitmapSource* source,
		UINT pixelSize,
		std::shared_ptr<const ArkheideSystem::ShellIconBitmapData>& bitmap)
	{
		if (factory == nullptr || source == nullptr) return E_POINTER;
		ComPtr<IWICBitmapSource> sizedSource(source);
		UINT width = 0;
		UINT height = 0;
		auto result = source->GetSize(&width, &height);
		if (FAILED(result)) return result;
		if (width != pixelSize || height != pixelSize)
		{
			ComPtr<IWICBitmapScaler> scaler;
			result = factory->CreateBitmapScaler(scaler.GetAddressOf());
			if (FAILED(result)) return result;
			result = scaler->Initialize(
				source,
				pixelSize,
				pixelSize,
				WICBitmapInterpolationModeFant);
			if (FAILED(result)) return result;
			sizedSource = scaler;
		}

		ComPtr<IWICFormatConverter> converter;
		result = factory->CreateFormatConverter(converter.GetAddressOf());
		if (FAILED(result)) return result;
		result = converter->Initialize(
			sizedSource.Get(),
			GUID_WICPixelFormat32bppPBGRA,
			WICBitmapDitherTypeNone,
			nullptr,
			0.0,
			WICBitmapPaletteTypeCustom);
		if (FAILED(result)) return result;

		const auto stride = pixelSize * 4U;
		const auto byteCount = static_cast<size_t>(stride) * pixelSize;
		auto next = std::make_shared<ArkheideSystem::ShellIconBitmapData>();
		next->width = pixelSize;
		next->height = pixelSize;
		next->stride = stride;
		next->pixels.resize(byteCount);
		result = converter->CopyPixels(
			nullptr,
			stride,
			static_cast<UINT>(byteCount),
			next->pixels.data());
		if (FAILED(result)) return result;
		bitmap = std::move(next);
		return S_OK;
	}

	HRESULT ConvertIcon(
		IWICImagingFactory* factory,
		HICON icon,
		UINT pixelSize,
		std::shared_ptr<const ArkheideSystem::ShellIconBitmapData>& bitmap)
	{
		if (icon == nullptr) return E_INVALIDARG;
		ComPtr<IWICBitmap> source;
		auto result = factory->CreateBitmapFromHICON(icon, source.GetAddressOf());
		if (FAILED(result)) return result;
		return ConvertBitmapSource(factory, source.Get(), pixelSize, bitmap);
	}

	HRESULT LoadStockIcon(
		IWICImagingFactory* factory,
		SHSTOCKICONID stockId,
		UINT pixelSize,
		std::shared_ptr<const ArkheideSystem::ShellIconBitmapData>& bitmap)
	{
		SHSTOCKICONINFO information{};
		information.cbSize = sizeof(information);
		auto result = SHGetStockIconInfo(
			stockId,
			SHGSI_ICON | SHGSI_LARGEICON,
			&information);
		if (FAILED(result)) return result;
		UniqueIconHandle icon(information.hIcon);
		return ConvertIcon(factory, icon.get(), pixelSize, bitmap);
	}

	HRESULT LoadShellItemIcon(
		IWICImagingFactory* factory,
		const IconKey& key,
		std::shared_ptr<const ArkheideSystem::ShellIconBitmapData>& bitmap)
	{
		if (key.source.empty()) return E_INVALIDARG;
		ComPtr<IShellItem> item;
		auto result = SHCreateItemFromParsingName(
			key.source.c_str(),
			nullptr,
			IID_PPV_ARGS(item.GetAddressOf()));
		if (FAILED(result)) return result;
		ComPtr<IShellItemImageFactory> imageFactory;
		result = item.As(&imageFactory);
		if (FAILED(result)) return result;
		HBITMAP handle = nullptr;
		const SIZE requestedSize{
			static_cast<LONG>(key.pixelSize),
			static_cast<LONG>(key.pixelSize),
		};
		result = imageFactory->GetImage(
			requestedSize,
			static_cast<SIIGBF>(SIIGBF_ICONONLY | SIIGBF_SCALEUP),
			&handle);
		if (FAILED(result)) return result;
		UniqueBitmapHandle ownedHandle(handle);

		ComPtr<IWICBitmap> source;
		result = factory->CreateBitmapFromHBITMAP(
			ownedHandle.get(),
			nullptr,
			WICBitmapUsePremultipliedAlpha,
			source.GetAddressOf());
		if (FAILED(result)) return result;
		return ConvertBitmapSource(factory, source.Get(), key.pixelSize, bitmap);
	}

	HRESULT LoadIcon(
		IWICImagingFactory* factory,
		const IconKey& key,
		std::shared_ptr<const ArkheideSystem::ShellIconBitmapData>& bitmap)
	{
		switch (key.sourceKind)
		{
		case ArkheideSystem::ShellIconSourceKind::GenericFile:
			return LoadStockIcon(factory, SIID_DOCNOASSOC, key.pixelSize, bitmap);
		case ArkheideSystem::ShellIconSourceKind::Folder:
			return LoadStockIcon(factory, SIID_FOLDER, key.pixelSize, bitmap);
		case ArkheideSystem::ShellIconSourceKind::Application:
			return LoadStockIcon(factory, SIID_APPLICATION, key.pixelSize, bitmap);
		case ArkheideSystem::ShellIconSourceKind::ShellItem:
			return LoadShellItemIcon(factory, key, bitmap);
		default:
			return E_INVALIDARG;
		}
	}
}

namespace ArkheideSystem
{
	class ShellIconLoader::State final
	{
	public:
		State()
			: wakeEvent_(CreateEventW(nullptr, TRUE, FALSE, nullptr))
		{
		}

		~State()
		{
			if (wakeEvent_ != nullptr) CloseHandle(wakeEvent_);
		}

		HRESULT Attach(HWND notificationWindow)
		{
			if (notificationWindow == nullptr || wakeEvent_ == nullptr) return E_INVALIDARG;
			std::lock_guard lock(mutex_);
			if (worker_.joinable()) return E_UNEXPECTED;
			notificationWindow_ = notificationWindow;
			stopping_ = false;
			try
			{
				worker_ = std::thread([this]() { Run(); });
			}
			catch (...)
			{
				notificationWindow_ = nullptr;
				return E_OUTOFMEMORY;
			}
			return S_OK;
		}

		void BeginGeneration(uint64_t generation) noexcept
		{
			std::lock_guard lock(mutex_);
			activeGeneration_ = generation;
			completed_.clear();
			queue_.clear();
			for (auto pending = pending_.begin(); pending != pending_.end();)
			{
				if (inFlightKey_.has_value()
					&& EquivalentIconKey(pending->first, *inFlightKey_))
				{
					pending->second.clear();
					++pending;
				}
				else
				{
					pending = pending_.erase(pending);
				}
			}
		}

		void Request(
			uint64_t token,
			uint64_t generation,
			ShellIconSourceKind sourceKind,
			const std::wstring& source,
			UINT pixelSize) noexcept
		{
			if (token == 0 || generation == 0) return;
			pixelSize = (std::clamp)(pixelSize, MinimumIconPixels, MaximumIconPixels);
			HWND notificationWindow = nullptr;
			bool notify = false;
			bool cacheHandled = false;
			bool queueAdded = false;
			try
			{
				IconKey key{ sourceKind, source, pixelSize };
				{
					std::lock_guard lock(mutex_);
					if (stopping_ || !worker_.joinable() || generation != activeGeneration_) return;
					const auto now = std::chrono::steady_clock::now();
					if (const auto cached = cache_.find(key); cached != cache_.end())
					{
						if (cached->second.expiresAt > now)
						{
							cached->second.lastAccess = ++accessSequence_;
							if (cached->second.bitmap && completed_.size() < MaximumCompletedIcons)
							{
								completed_.push_back(ShellIconResult{
									token,
									generation,
									sourceKind,
									key.source,
									pixelSize,
									cached->second.bitmap,
								});
								notificationWindow = notificationWindow_;
								notify = true;
							}
							cacheHandled = true;
						}
						else
						{
							cache_.erase(cached);
						}
					}

					if (!cacheHandled)
					{
						if (auto pending = pending_.find(key); pending != pending_.end())
						{
							const auto duplicate = std::ranges::any_of(
								pending->second,
								[token, generation](const Subscriber& subscriber)
								{
									return subscriber.token == token
										&& subscriber.generation == generation;
								});
							if (!duplicate && pending->second.size() < 32)
							{
								pending->second.push_back(Subscriber{ token, generation });
							}
							return;
						}
						if (queue_.size() >= MaximumPendingIcons) return;
						pending_.emplace(key, std::vector<Subscriber>{ Subscriber{ token, generation } });
						queue_.push_back(std::move(key));
						queueAdded = true;
					}
				}
				if (cacheHandled)
				{
					NotifyAfterUnlock(notificationWindow, notify);
					return;
				}
				if (queueAdded) SetEvent(wakeEvent_);
			}
			catch (...)
			{
				return;
			}
		}

		std::vector<ShellIconResult> TakeCompleted() noexcept
		{
			std::vector<ShellIconResult> completed;
			try
			{
				std::lock_guard lock(mutex_);
				completed.assign(
					std::make_move_iterator(completed_.begin()),
					std::make_move_iterator(completed_.end()));
				completed_.clear();
			}
			catch (...)
			{
				completed.clear();
			}
			return completed;
		}

		bool Shutdown() noexcept
		{
			{
				std::lock_guard lock(mutex_);
				stopping_ = true;
				notificationWindow_ = nullptr;
				queue_.clear();
				completed_.clear();
				for (auto& pending : pending_) pending.second.clear();
			}
			if (wakeEvent_ != nullptr) SetEvent(wakeEvent_);
			if (worker_.joinable())
			{
				const auto waitResult = WaitForSingleObject(
					worker_.native_handle(),
					WorkerShutdownTimeoutMs);
				if (waitResult != WAIT_OBJECT_0)
				{
					worker_.detach();
					return false;
				}
				worker_.join();
			}
			std::lock_guard lock(mutex_);
			pending_.clear();
			cache_.clear();
			return true;
		}

	private:
		static void NotifyAfterUnlock(HWND notificationWindow, bool notify) noexcept
		{
			if (notify && notificationWindow != nullptr)
			{
				PostMessageW(notificationWindow, ShellIconLoader::CompletionMessage, 0, 0);
			}
		}

		void Run() noexcept
		{
			const auto comResult = CoInitializeEx(
				nullptr,
				COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
			const auto uninitialize = SUCCEEDED(comResult);
			ComPtr<IWICImagingFactory> factory;
			if (SUCCEEDED(comResult) || comResult == RPC_E_CHANGED_MODE)
			{
				(void)CoCreateInstance(
					CLSID_WICImagingFactory,
					nullptr,
					CLSCTX_INPROC_SERVER,
					IID_PPV_ARGS(factory.GetAddressOf()));
			}

			MSG message{};
			PeekMessageW(&message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
			while (true)
			{
				IconKey key;
				bool hasWork = false;
				{
					std::lock_guard lock(mutex_);
					if (stopping_) break;
					if (!queue_.empty())
					{
						key = std::move(queue_.front());
						queue_.pop_front();
						try
						{
							inFlightKey_ = key;
							hasWork = true;
						}
						catch (...)
						{
							inFlightKey_.reset();
							pending_.erase(key);
						}
					}
					else if (wakeEvent_ != nullptr)
					{
						ResetEvent(wakeEvent_);
					}
				}

				if (hasWork)
				{
					std::shared_ptr<const ShellIconBitmapData> bitmap;
					try
					{
						if (factory) (void)LoadIcon(factory.Get(), key, bitmap);
					}
					catch (...)
					{
						bitmap.reset();
					}
					Complete(key, std::move(bitmap));
					PumpMessages();
					continue;
				}

				HANDLE handles[]{ wakeEvent_ };
				const auto waitResult = MsgWaitForMultipleObjectsEx(
					1,
					handles,
					INFINITE,
					QS_ALLINPUT,
					MWMO_INPUTAVAILABLE);
				if (waitResult == WAIT_OBJECT_0 + 1)
				{
					PumpMessages();
				}
				else if (waitResult == WAIT_FAILED)
				{
					break;
				}
			}

			factory.Reset();
			if (uninitialize) CoUninitialize();
		}

		static void PumpMessages() noexcept
		{
			MSG message{};
			while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
			{
				TranslateMessage(&message);
				DispatchMessageW(&message);
			}
		}

		void Complete(
			const IconKey& key,
			std::shared_ptr<const ShellIconBitmapData> bitmap) noexcept
		{
			HWND notificationWindow = nullptr;
			bool notify = false;
			try
			{
				std::lock_guard lock(mutex_);
				inFlightKey_.reset();
				const auto pending = pending_.find(key);
				if (pending == pending_.end()) return;
				auto subscribers = std::move(pending->second);
				pending_.erase(pending);
				const auto now = std::chrono::steady_clock::now();
				cache_[key] = CacheEntry{
					bitmap,
					now + (bitmap ? SuccessfulCacheLifetime : FailedCacheLifetime),
					++accessSequence_,
				};
				TrimCache();
				if (bitmap)
				{
					for (const auto& subscriber : subscribers)
					{
						if (subscriber.generation != activeGeneration_
							|| completed_.size() >= MaximumCompletedIcons)
						{
							continue;
						}
						completed_.push_back(ShellIconResult{
							subscriber.token,
							subscriber.generation,
							key.sourceKind,
							key.source,
							key.pixelSize,
							bitmap,
						});
						notify = true;
					}
				}
				notificationWindow = notificationWindow_;
			}
			catch (...)
			{
				return;
			}
			NotifyAfterUnlock(notificationWindow, notify);
		}

		void TrimCache() noexcept
		{
			while (cache_.size() > MaximumCachedIcons)
			{
				auto oldest = cache_.begin();
				for (auto current = std::next(cache_.begin()); current != cache_.end(); ++current)
				{
					if (current->second.lastAccess < oldest->second.lastAccess) oldest = current;
				}
				cache_.erase(oldest);
			}
		}

		std::mutex mutex_;
		std::thread worker_;
		HANDLE wakeEvent_ = nullptr;
		HWND notificationWindow_ = nullptr;
		bool stopping_ = false;
		uint64_t activeGeneration_ = 0;
		uint64_t accessSequence_ = 0;
		std::deque<IconKey> queue_;
		std::optional<IconKey> inFlightKey_;
		std::map<IconKey, std::vector<Subscriber>, IconKeyLess> pending_;
		std::map<IconKey, CacheEntry, IconKeyLess> cache_;
		std::vector<ShellIconResult> completed_;
	};

	ShellIconLoader::ShellIconLoader()
		: state_(std::make_unique<State>())
	{
	}

	ShellIconLoader::~ShellIconLoader()
	{
		Shutdown();
	}

	HRESULT ShellIconLoader::Attach(HWND notificationWindow)
	{
		return state_ ? state_->Attach(notificationWindow) : E_OUTOFMEMORY;
	}

	void ShellIconLoader::BeginGeneration(uint64_t generation) noexcept
	{
		if (state_) state_->BeginGeneration(generation);
	}

	void ShellIconLoader::Request(
		uint64_t token,
		uint64_t generation,
		ShellIconSourceKind sourceKind,
		const std::wstring& source,
		UINT pixelSize) noexcept
	{
		if (state_)
		{
			state_->Request(
				token,
				generation,
				sourceKind,
				source,
				pixelSize);
		}
	}

	std::vector<ShellIconResult> ShellIconLoader::TakeCompleted() noexcept
	{
		return state_ ? state_->TakeCompleted() : std::vector<ShellIconResult>{};
	}

	void ShellIconLoader::Shutdown() noexcept
	{
		if (state_ && !state_->Shutdown())
		{
			// A third-party Shell icon handler cannot be cancelled safely. Keep the
			// detached worker state alive until process exit rather than blocking the
			// native host's bounded shutdown or allowing a use-after-free.
			(void)state_.release();
		}
	}
}

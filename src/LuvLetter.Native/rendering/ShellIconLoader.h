#pragma once

#include <Windows.h>

#include <cstdint>
#include <memory>
#include <string>
#include <vector>

namespace ArkheideSystem
{
	enum class ShellIconSourceKind : uint8_t
	{
		GenericFile,
		Folder,
		Application,
		ShellItem,
	};

	struct ShellIconBitmapData final
	{
		UINT width = 0;
		UINT height = 0;
		UINT stride = 0;
		std::vector<uint8_t> pixels;
	};

	struct ShellIconResult final
	{
		uint64_t token = 0;
		uint64_t generation = 0;
		ShellIconSourceKind sourceKind = ShellIconSourceKind::GenericFile;
		std::wstring source;
		UINT pixelSize = 0;
		std::shared_ptr<const ShellIconBitmapData> bitmap;
	};

	class ShellIconLoader final
	{
	public:
		static constexpr UINT CompletionMessage = WM_APP + 42;

		ShellIconLoader();
		~ShellIconLoader();
		ShellIconLoader(const ShellIconLoader&) = delete;
		ShellIconLoader& operator=(const ShellIconLoader&) = delete;

		HRESULT Attach(HWND notificationWindow);
		void BeginGeneration(uint64_t generation) noexcept;
		void Request(
			uint64_t token,
			uint64_t generation,
			ShellIconSourceKind sourceKind,
			const std::wstring& source,
			UINT pixelSize) noexcept;
		std::vector<ShellIconResult> TakeCompleted() noexcept;
		void Shutdown() noexcept;

	private:
		class State;
		std::unique_ptr<State> state_;
	};
}

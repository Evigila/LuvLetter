#pragma once

#include <cstdint>

#ifdef _WIN32
#define LUVLETTER_NATIVE_CALL __stdcall
#define LUVLETTER_NATIVE_EXPORT __declspec(dllexport)
#else
#define LUVLETTER_NATIVE_CALL
#define LUVLETTER_NATIVE_EXPORT
#endif

extern "C"
{
	inline constexpr uint32_t LUVLETTER_NATIVE_ABI_VERSION = 6;

	enum LuvLetterInputMode : int32_t
	{
		LuvLetterInputModeGeneral = 0,
		LuvLetterInputModeAsk = 1,
		LuvLetterInputModeCommand = 2,
	};

	enum LuvLetterCandidateKind : int32_t
	{
		LuvLetterCandidateKindFile = 1,
		LuvLetterCandidateKindCommand = 2,
		LuvLetterCandidateKindGlobalSearch = 3,
	};

	enum LuvLetterCandidateAction : int32_t
	{
		LuvLetterCandidateActionOpen = 0,
		LuvLetterCandidateActionReveal = 1,
	};

	enum LuvLetterCandidateIconKind : int32_t
	{
		LuvLetterCandidateIconKindNone = 0,
		LuvLetterCandidateIconKindGenericFile = 1,
		LuvLetterCandidateIconKindFolder = 2,
		LuvLetterCandidateIconKindImage = 3,
		LuvLetterCandidateIconKindDocument = 4,
		LuvLetterCandidateIconKindArchive = 5,
		LuvLetterCandidateIconKindAudio = 6,
		LuvLetterCandidateIconKindVideo = 7,
		LuvLetterCandidateIconKindExecutable = 8,
		LuvLetterCandidateIconKindCommand = 9,
		LuvLetterCandidateIconKindSearch = 10,
	};

	struct LuvLetterInputBoxConfig
	{
		uint32_t structSize;
		uint32_t abiVersion;
		int32_t width;
		int32_t height;
		float cornerRadius;
		float borderThickness;
		float fontSize;
		float horizontalPadding;
		float verticalPadding;
		float caretWidth;
		int32_t positionMode;
		int32_t offsetX;
		int32_t offsetY;
		int32_t bottomMargin;
		int32_t customX;
		int32_t customY;
		uint32_t borderColor;
		uint32_t backgroundColor;
		uint32_t textColor;
		uint32_t caretColor;
		int32_t submitVirtualKey;
		int32_t cancelVirtualKey;
		int32_t backspaceVirtualKey;
		int32_t submitModifiers;
		int32_t cancelModifiers;
		int32_t backspaceModifiers;
	};

	struct LuvLetterFeatureWindowConfig
	{
		uint32_t structSize;
		uint32_t abiVersion;
		int32_t itemsPerPage;
		float cellSize;
		float gap;
		float cornerRadius;
		float borderThickness;
		float fontSize;
		int32_t bottomMargin;
		int32_t offsetX;
		int32_t offsetY;
		uint32_t borderColor;
		uint32_t backgroundColor;
		uint32_t textColor;
		uint32_t accentColor;
		int32_t previousVirtualKey;
		int32_t nextVirtualKey;
		int32_t cancelVirtualKey;
		int32_t firstItemVirtualKey;
		int32_t previousModifiers;
		int32_t nextModifiers;
		int32_t cancelModifiers;
	};

	struct LuvLetterFeatureItem
	{
		uint64_t token;
		const wchar_t* label;
	};

	struct LuvLetterInputCandidate
	{
		uint64_t token;
		int32_t kind;
		int32_t iconKind;
		const wchar_t* primaryText;
		const wchar_t* secondaryText;
	};

	using LuvLetterFeatureActivatedCallback = void (LUVLETTER_NATIVE_CALL*)(
		uint64_t token,
		void* context);
	using LuvLetterInputSubmittedCallback = void (LUVLETTER_NATIVE_CALL*)(
		const wchar_t* text,
		int32_t length,
		int32_t inputMode,
		void* context);
	using LuvLetterInputChangedCallback = void (LUVLETTER_NATIVE_CALL*)(
		const wchar_t* text,
		int32_t length,
		int32_t inputMode,
		uint64_t revision,
		void* context);
	using LuvLetterCandidateActivatedCallback = void (LUVLETTER_NATIVE_CALL*)(
		uint64_t token,
		int32_t action,
		void* context);

	LUVLETTER_NATIVE_EXPORT uint32_t LUVLETTER_NATIVE_CALL GetNativeApiVersion();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ApplyInputBoxConfig(
		const LuvLetterInputBoxConfig* config);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL SetInputSubmittedCallback(
		LuvLetterInputSubmittedCallback callback,
		void* context);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL SetInputChangedCallback(
		LuvLetterInputChangedCallback callback,
		void* context);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL SetCandidateActivatedCallback(
		LuvLetterCandidateActivatedCallback callback,
		void* context);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL SetInputCandidates(
		const LuvLetterInputCandidate* items,
		int32_t count,
		uint64_t revision);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ShowInputBox();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL HideInputBox();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ToggleInputBox();

	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ApplyFeatureWindowConfig(
		const LuvLetterFeatureWindowConfig* config);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL SetFeatureItems(
		const LuvLetterFeatureItem* items,
		int32_t count);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL SetFeatureActivatedCallback(
		LuvLetterFeatureActivatedCallback callback,
		void* context);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ShowFeatureWindow();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL HideFeatureWindow();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ToggleFeatureWindow();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL EnqueueMessage(
		const wchar_t* text,
		int32_t length);
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ToggleMessageQueue();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL HideMessageQueue();
	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL HidePopups();

	LUVLETTER_NATIVE_EXPORT int LUVLETTER_NATIVE_CALL ShutdownInputBox();
}

static_assert(sizeof(LuvLetterInputBoxConfig) == 104);
static_assert(sizeof(LuvLetterFeatureWindowConfig) == 88);
static_assert(sizeof(LuvLetterFeatureItem) == 16);
static_assert(sizeof(LuvLetterInputCandidate) == 32);

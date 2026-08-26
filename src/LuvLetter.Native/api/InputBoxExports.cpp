#include "api/InputBoxApi.h"
#include "host/NativeShellHost.h"

#include <Windows.h>

uint32_t LUVLETTER_NATIVE_CALL GetNativeApiVersion()
{
	return LUVLETTER_NATIVE_ABI_VERSION;
}

int LUVLETTER_NATIVE_CALL ApplyInputBoxConfig(const LuvLetterInputBoxConfig* config)
{
	if (config == nullptr) return E_INVALIDARG;
	try
	{
		return NativeShellHost::Instance().ApplyConfig(*config);
	}
	catch (...)
	{
		return E_FAIL;
	}
}

int LUVLETTER_NATIVE_CALL SetInputSubmittedCallback(
	LuvLetterInputSubmittedCallback callback,
	void* context)
{
	try
	{
		return NativeShellHost::Instance().SetInputSubmittedCallback(callback, context);
	}
	catch (...)
	{
		return E_FAIL;
	}
}

int LUVLETTER_NATIVE_CALL ShowInputBox()
{
	try { return NativeShellHost::Instance().Show(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL HideInputBox()
{
	try { return NativeShellHost::Instance().Hide(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ToggleInputBox()
{
	try { return NativeShellHost::Instance().Toggle(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ApplyFeatureWindowConfig(const LuvLetterFeatureWindowConfig* config)
{
	if (config == nullptr) return E_INVALIDARG;
	try
	{
		return NativeShellHost::Instance().ApplyQuickActionsConfig(*config);
	}
	catch (...)
	{
		return E_FAIL;
	}
}

int LUVLETTER_NATIVE_CALL SetFeatureItems(const LuvLetterFeatureItem* items, int32_t count)
{
	try
	{
		return NativeShellHost::Instance().SetQuickActions(items, count);
	}
	catch (...)
	{
		return E_FAIL;
	}
}

int LUVLETTER_NATIVE_CALL SetFeatureActivatedCallback(
	LuvLetterFeatureActivatedCallback callback,
	void* context)
{
	try
	{
		return NativeShellHost::Instance().SetQuickActionActivatedCallback(callback, context);
	}
	catch (...)
	{
		return E_FAIL;
	}
}

int LUVLETTER_NATIVE_CALL ShowFeatureWindow()
{
	try { return NativeShellHost::Instance().ShowQuickActionsWindow(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL HideFeatureWindow()
{
	try { return NativeShellHost::Instance().HideQuickActionsWindow(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ToggleFeatureWindow()
{
	try { return NativeShellHost::Instance().ToggleQuickActionsWindow(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ShutdownInputBox()
{
	try { return NativeShellHost::Instance().Shutdown(); }
	catch (...) { return E_FAIL; }
}

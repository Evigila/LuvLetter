#include "api/InputBoxApi.h"
#include "input/InputBoxHost.h"

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
		return InputBoxHost::Instance().ApplyConfig(*config);
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
		return InputBoxHost::Instance().SetInputSubmittedCallback(callback, context);
	}
	catch (...)
	{
		return E_FAIL;
	}
}

int LUVLETTER_NATIVE_CALL ShowInputBox()
{
	try { return InputBoxHost::Instance().Show(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL HideInputBox()
{
	try { return InputBoxHost::Instance().Hide(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ToggleInputBox()
{
	try { return InputBoxHost::Instance().Toggle(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ApplyFeatureWindowConfig(const LuvLetterFeatureWindowConfig* config)
{
	if (config == nullptr) return E_INVALIDARG;
	try
	{
		return InputBoxHost::Instance().ApplyFeatureConfig(*config);
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
		return InputBoxHost::Instance().SetFeatureItems(items, count);
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
		return InputBoxHost::Instance().SetFeatureActivatedCallback(callback, context);
	}
	catch (...)
	{
		return E_FAIL;
	}
}

int LUVLETTER_NATIVE_CALL ShowFeatureWindow()
{
	try { return InputBoxHost::Instance().ShowFeatureWindow(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL HideFeatureWindow()
{
	try { return InputBoxHost::Instance().HideFeatureWindow(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ToggleFeatureWindow()
{
	try { return InputBoxHost::Instance().ToggleFeatureWindow(); }
	catch (...) { return E_FAIL; }
}

int LUVLETTER_NATIVE_CALL ShutdownInputBox()
{
	try { return InputBoxHost::Instance().Shutdown(); }
	catch (...) { return E_FAIL; }
}

#include "api/InputBoxApi.h"
#include "input/InputBoxHost.h"

int LUVLETTER_NATIVE_CALL ShowInputBox()
{
	return InputBoxHost::Instance().Show();
}

int LUVLETTER_NATIVE_CALL HideInputBox()
{
	return InputBoxHost::Instance().Hide();
}

int LUVLETTER_NATIVE_CALL ToggleInputBox()
{
	return InputBoxHost::Instance().Toggle();
}

void LUVLETTER_NATIVE_CALL ShutdownInputBox()
{
	InputBoxHost::Instance().Shutdown();
}

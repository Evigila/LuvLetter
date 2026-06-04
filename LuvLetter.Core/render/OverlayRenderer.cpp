#include "render/OverlayRenderer.h"

#include <algorithm>

HRESULT OverlayRenderer::Initialize()
{
	if (!d2dFactory_)
	{
		auto hr = D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2dFactory_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!wicFactory_)
	{
		auto hr = CoCreateInstance(
			CLSID_WICImagingFactory,
			nullptr,
			CLSCTX_INPROC_SERVER,
			IID_PPV_ARGS(wicFactory_.GetAddressOf()));
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!dwriteFactory_)
	{
		auto hr = DWriteCreateFactory(
			DWRITE_FACTORY_TYPE_SHARED,
			__uuidof(IDWriteFactory),
			reinterpret_cast<IUnknown**>(dwriteFactory_.GetAddressOf()));
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!inputTextFormat_)
	{
		auto hr = dwriteFactory_->CreateTextFormat(
			L"Segoe UI",
			nullptr,
			DWRITE_FONT_WEIGHT_REGULAR,
			DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL,
			18.0f,
			L"",
			inputTextFormat_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}

		inputTextFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		inputTextFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
	}

	if (!outputTextFormat_)
	{
		auto hr = dwriteFactory_->CreateTextFormat(
			L"Segoe UI",
			nullptr,
			DWRITE_FONT_WEIGHT_REGULAR,
			DWRITE_FONT_STYLE_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL,
			16.0f,
			L"",
			outputTextFormat_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}

		outputTextFormat_->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
		outputTextFormat_->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
	}

	return S_OK;
}

HRESULT OverlayRenderer::EnsureResources(HWND hwnd, const OverlayState& state)
{
	auto hr = Initialize();
	if (FAILED(hr))
	{
		return hr;
	}

	if (!renderTarget_)
	{
		RECT rect{};
		GetClientRect(hwnd, &rect);
		hr = d2dFactory_->CreateHwndRenderTarget(
			D2D1::RenderTargetProperties(),
			D2D1::HwndRenderTargetProperties(
				hwnd,
				D2D1::SizeU(rect.right - rect.left, rect.bottom - rect.top),
				D2D1_PRESENT_OPTIONS_IMMEDIATELY),
			renderTarget_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!textBrush_)
	{
		hr = renderTarget_->CreateSolidColorBrush(
			D2D1::ColorF(D2D1::ColorF::White),
			textBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!outputPanelBrush_)
	{
		hr = renderTarget_->CreateSolidColorBrush(
			D2D1::ColorF(0.20f, 0.20f, 0.20f, 0.94f),
			outputPanelBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!separatorBrush_)
	{
		hr = renderTarget_->CreateSolidColorBrush(
			D2D1::ColorF(0.62f, 0.62f, 0.62f, 0.90f),
			separatorBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (state.logoBytes != cachedLogoBytes_ || !logoBitmap_)
	{
		cachedLogoBytes_ = state.logoBytes;
		logoBitmap_.Reset();
		hr = CreateLogoBitmap(cachedLogoBytes_);
		if (FAILED(hr))
		{
			return hr;
		}
	}

	return S_OK;
}

void OverlayRenderer::Resize(UINT width, UINT height)
{
	if (renderTarget_)
	{
		renderTarget_->Resize(D2D1::SizeU(width, height));
	}
}

void OverlayRenderer::ResetLogoBitmap()
{
	logoBitmap_.Reset();
	cachedLogoBytes_.clear();
}

void OverlayRenderer::DiscardDeviceResources()
{
	textBrush_.Reset();
	outputPanelBrush_.Reset();
	separatorBrush_.Reset();
	logoBitmap_.Reset();
	renderTarget_.Reset();
}

void OverlayRenderer::Render(HWND hwnd, const OverlayState& state, const OverlayLayoutSnapshot& layoutSnapshot)
{
	const auto hr = EnsureResources(hwnd, state);
	if (FAILED(hr))
	{
		return;
	}

	renderTarget_->BeginDraw();
	renderTarget_->Clear(D2D1::ColorF(0.38f, 0.38f, 0.38f, 1.0f));

	if (state.visualMode == LuvLetterOverlayVisualMode_CommandLine)
	{
		if (layoutSnapshot.hasOutputArea)
		{
			renderTarget_->FillRectangle(layoutSnapshot.outputPanelRect, outputPanelBrush_.Get());

			const auto separatorY = layoutSnapshot.outputPanelRect.bottom + 4.0f;
			renderTarget_->DrawLine(
				D2D1::Point2F(0.0f, separatorY),
				D2D1::Point2F(layoutSnapshot.backgroundRect.right, separatorY),
				separatorBrush_.Get(),
				1.0f);
		}

		if (layoutSnapshot.inputPromptRect.right > layoutSnapshot.inputPromptRect.left)
		{
			static constexpr wchar_t Prompt[] = L">";
			renderTarget_->DrawTextW(
				Prompt,
				1,
				inputTextFormat_.Get(),
				layoutSnapshot.inputPromptRect,
				textBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}

		if (!state.inputText.empty() && layoutSnapshot.inputTextRect.right > layoutSnapshot.inputTextRect.left)
		{
			renderTarget_->DrawTextW(
				state.inputText.c_str(),
				static_cast<UINT32>(state.inputText.size()),
				inputTextFormat_.Get(),
				layoutSnapshot.inputTextRect,
				textBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}

		if (!state.outputText.empty() && layoutSnapshot.hasOutputArea)
		{
			renderTarget_->DrawTextW(
				state.outputText.c_str(),
				static_cast<UINT32>(state.outputText.size()),
				outputTextFormat_.Get(),
				layoutSnapshot.outputTextRect,
				textBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}
	}

	if (logoBitmap_)
	{
		renderTarget_->DrawBitmap(
			logoBitmap_.Get(),
			layoutSnapshot.logoRect,
			1.0f,
			D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
	}

	const auto endDrawResult = renderTarget_->EndDraw();
	if (endDrawResult == D2DERR_RECREATE_TARGET)
	{
		DiscardDeviceResources();
	}
}

HRESULT OverlayRenderer::CreateLogoBitmap(const std::vector<uint8_t>& logoBytes)
{
	if (!renderTarget_)
	{
		return E_UNEXPECTED;
	}

	if (logoBytes.empty())
	{
		return E_INVALIDARG;
	}

	Microsoft::WRL::ComPtr<IWICStream> stream;
	auto hr = wicFactory_->CreateStream(stream.GetAddressOf());
	if (FAILED(hr))
	{
		return hr;
	}

	hr = stream->InitializeFromMemory(
		const_cast<BYTE*>(logoBytes.data()),
		static_cast<DWORD>(logoBytes.size()));
	if (FAILED(hr))
	{
		return hr;
	}

	Microsoft::WRL::ComPtr<IWICBitmapDecoder> decoder;
	hr = wicFactory_->CreateDecoderFromStream(
		stream.Get(),
		nullptr,
		WICDecodeMetadataCacheOnLoad,
		decoder.GetAddressOf());
	if (FAILED(hr))
	{
		return hr;
	}

	UINT frameCount = 0;
	hr = decoder->GetFrameCount(&frameCount);
	if (FAILED(hr) || frameCount == 0)
	{
		return FAILED(hr) ? hr : E_FAIL;
	}

	Microsoft::WRL::ComPtr<IWICBitmapFrameDecode> selectedFrame;
	UINT selectedArea = 0;
	for (UINT index = 0; index < frameCount; ++index)
	{
		Microsoft::WRL::ComPtr<IWICBitmapFrameDecode> frame;
		hr = decoder->GetFrame(index, frame.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}

		UINT width = 0;
		UINT height = 0;
		hr = frame->GetSize(&width, &height);
		if (FAILED(hr))
		{
			return hr;
		}

		const auto area = width * height;
		if (!selectedFrame || area > selectedArea)
		{
			selectedArea = area;
			selectedFrame = frame;
		}
	}

	Microsoft::WRL::ComPtr<IWICFormatConverter> converter;
	hr = wicFactory_->CreateFormatConverter(converter.GetAddressOf());
	if (FAILED(hr))
	{
		return hr;
	}

	hr = converter->Initialize(
		selectedFrame.Get(),
		GUID_WICPixelFormat32bppPBGRA,
		WICBitmapDitherTypeNone,
		nullptr,
		0.0,
		WICBitmapPaletteTypeCustom);
	if (FAILED(hr))
	{
		return hr;
	}

	return renderTarget_->CreateBitmapFromWicBitmap(converter.Get(), logoBitmap_.GetAddressOf());
}

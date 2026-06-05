#include "render/OverlayRenderer.h"

#include <algorithm>

namespace
{
	constexpr D2D1_COLOR_F ContainerBackgroundColor{ 0.9490f, 0.9255f, 0.8980f, 1.0f };
	constexpr D2D1_COLOR_F TextColor{ 0.21f, 0.18f, 0.16f, 1.0f };
	constexpr D2D1_COLOR_F PlaceholderColor{ 0.53f, 0.49f, 0.45f, 0.85f };
	constexpr D2D1_COLOR_F SelectionColor{ 0.86f, 0.79f, 0.69f, 0.92f };
	constexpr D2D1_COLOR_F SeparatorColor{ 0.72f, 0.68f, 0.63f, 0.95f };
	constexpr D2D1_COLOR_F TransparentColor{ 0.0f, 0.0f, 0.0f, 0.0f };
	constexpr wchar_t PlaceholderText[] = L"Type help for more tips";

	D2D1_RECT_F InsetRect(const D2D1_RECT_F& rect, float horizontalInset, float verticalInset)
	{
		return D2D1::RectF(
			rect.left + horizontalInset,
			rect.top + verticalInset,
			(std::max)(rect.left + horizontalInset, rect.right - horizontalInset),
			(std::max)(rect.top + verticalInset, rect.bottom - verticalInset));
	}

	float ClampNonNegative(float value)
	{
		return (std::max)(0.0f, value);
	}
}

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
			TextColor,
			textBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!placeholderBrush_)
	{
		hr = renderTarget_->CreateSolidColorBrush(
			PlaceholderColor,
			placeholderBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!selectionBrush_)
	{
		hr = renderTarget_->CreateSolidColorBrush(
			SelectionColor,
			selectionBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!outputPanelBrush_)
	{
		hr = renderTarget_->CreateSolidColorBrush(
			ContainerBackgroundColor,
			outputPanelBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!separatorBrush_)
	{
		hr = renderTarget_->CreateSolidColorBrush(
			SeparatorColor,
			separatorBrush_.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}
	}

	if (!inputBarGradientBrush_)
	{
		const D2D1_GRADIENT_STOP gradientStops[] = {
			{ 0.0f, ContainerBackgroundColor },
			{ 0.84f, ContainerBackgroundColor },
			{ 0.96f, D2D1::ColorF(ContainerBackgroundColor.r, ContainerBackgroundColor.g, ContainerBackgroundColor.b, 0.22f) },
			{ 1.0f, TransparentColor },
		};

		Microsoft::WRL::ComPtr<ID2D1GradientStopCollection> stopCollection;
		hr = renderTarget_->CreateGradientStopCollection(
			gradientStops,
			static_cast<UINT32>(_countof(gradientStops)),
			D2D1_GAMMA_2_2,
			D2D1_EXTEND_MODE_CLAMP,
			stopCollection.GetAddressOf());
		if (FAILED(hr))
		{
			return hr;
		}

		const D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES gradientBrushProperties =
			D2D1::LinearGradientBrushProperties(
				D2D1::Point2F(0.0f, 0.0f),
				D2D1::Point2F(1.0f, 0.0f));
		hr = renderTarget_->CreateLinearGradientBrush(
			&gradientBrushProperties,
			nullptr,
			stopCollection.Get(),
			inputBarGradientBrush_.GetAddressOf());
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
	placeholderBrush_.Reset();
	selectionBrush_.Reset();
	outputPanelBrush_.Reset();
	separatorBrush_.Reset();
	inputBarGradientBrush_.Reset();
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
	renderTarget_->Clear(
		state.visualMode == LuvLetterOverlayVisualMode_CommandLine ? TransparentColor : ContainerBackgroundColor);

	if (state.visualMode == LuvLetterOverlayVisualMode_CommandLine)
	{
		const auto inputTextWidth = ClampNonNegative(layoutSnapshot.inputTextRect.right - layoutSnapshot.inputTextRect.left);
		const auto inputTextHeight = ClampNonNegative(layoutSnapshot.inputTextRect.bottom - layoutSnapshot.inputTextRect.top);

		if (inputBarGradientBrush_)
		{
			inputBarGradientBrush_->SetStartPoint(
				D2D1::Point2F(layoutSnapshot.inputBarRect.left, layoutSnapshot.inputBarRect.top));
			inputBarGradientBrush_->SetEndPoint(
				D2D1::Point2F(layoutSnapshot.inputBarRect.right, layoutSnapshot.inputBarRect.top));
			renderTarget_->FillRectangle(layoutSnapshot.inputBarRect, inputBarGradientBrush_.Get());
		}

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
			renderTarget_->DrawTextW(
				state.inputPromptText.c_str(),
				static_cast<UINT32>(state.inputPromptText.size()),
				inputTextFormat_.Get(),
				layoutSnapshot.inputPromptRect,
				textBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}

		renderTarget_->PushAxisAlignedClip(layoutSnapshot.inputTextRect, D2D1_ANTIALIAS_MODE_ALIASED);

		Microsoft::WRL::ComPtr<IDWriteTextLayout> inputTextLayout;
		DWRITE_TEXT_METRICS inputMetrics{};
		auto inputTextOriginX = layoutSnapshot.inputTextRect.left;
		auto inputTextOriginY = layoutSnapshot.inputTextRect.top;
		auto inputHorizontalOffset = 0.0f;
		if (!state.inputText.empty() && inputTextWidth > 0.0f && inputTextHeight > 0.0f)
		{
			if (SUCCEEDED(dwriteFactory_->CreateTextLayout(
					state.inputText.c_str(),
					static_cast<UINT32>(state.inputText.size()),
					inputTextFormat_.Get(),
					4096.0f,
					inputTextHeight,
					inputTextLayout.GetAddressOf())))
			{
				inputTextLayout->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
				if (SUCCEEDED(inputTextLayout->GetMetrics(&inputMetrics)))
				{
					DWRITE_HIT_TEST_METRICS caretMetrics{};
					float caretPointX = 0.0f;
					float caretPointY = 0.0f;
					if (SUCCEEDED(inputTextLayout->HitTestTextPosition(
							static_cast<UINT32>(state.inputCaretIndex),
							FALSE,
							&caretPointX,
							&caretPointY,
							&caretMetrics)))
					{
						const auto caretPadding = 18.0f;
						const auto maxOffset = (std::max)(0.0f, inputMetrics.widthIncludingTrailingWhitespace - inputTextWidth);
						const auto desiredOffset = (std::max)(0.0f, caretPointX - inputTextWidth + caretPadding);
						inputHorizontalOffset = (std::min)(maxOffset, desiredOffset);
					}

					inputTextOriginX = layoutSnapshot.inputTextRect.left - inputHorizontalOffset;
					inputTextOriginY = layoutSnapshot.inputTextRect.top +
						ClampNonNegative((inputTextHeight - inputMetrics.height) * 0.5f);
				}
			}
		}

		if (inputTextLayout && state.inputSelectionLength > 0)
		{
			std::vector<DWRITE_HIT_TEST_METRICS> selectionMetrics(
				static_cast<size_t>((std::max)(1, state.inputSelectionLength + 1)));
			UINT32 actualMetricsCount = 0;
			auto hitTestResult = inputTextLayout->HitTestTextRange(
				static_cast<UINT32>(state.inputSelectionStart),
				static_cast<UINT32>(state.inputSelectionLength),
				inputTextOriginX,
				inputTextOriginY,
				selectionMetrics.data(),
				static_cast<UINT32>(selectionMetrics.size()),
				&actualMetricsCount);
			if (hitTestResult == E_NOT_SUFFICIENT_BUFFER)
			{
				selectionMetrics.resize(actualMetricsCount);
				hitTestResult = inputTextLayout->HitTestTextRange(
					static_cast<UINT32>(state.inputSelectionStart),
					static_cast<UINT32>(state.inputSelectionLength),
					inputTextOriginX,
					inputTextOriginY,
					selectionMetrics.data(),
					static_cast<UINT32>(selectionMetrics.size()),
					&actualMetricsCount);
			}

			if (SUCCEEDED(hitTestResult))
			{
				for (UINT32 index = 0; index < actualMetricsCount; ++index)
				{
					const auto& metric = selectionMetrics[index];
					renderTarget_->FillRectangle(
						D2D1::RectF(
							metric.left,
							metric.top,
							metric.left + metric.width,
							metric.top + metric.height),
						selectionBrush_.Get());
				}
			}
		}

		if (inputTextLayout)
		{
			renderTarget_->DrawTextLayout(
				D2D1::Point2F(inputTextOriginX, inputTextOriginY),
				inputTextLayout.Get(),
				textBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP);
		}
		else if (layoutSnapshot.inputTextRect.right > layoutSnapshot.inputTextRect.left)
		{
			renderTarget_->DrawTextW(
				PlaceholderText,
				static_cast<UINT32>(_countof(PlaceholderText) - 1),
				inputTextFormat_.Get(),
				layoutSnapshot.inputTextRect,
				placeholderBrush_.Get(),
				D2D1_DRAW_TEXT_OPTIONS_CLIP,
				DWRITE_MEASURING_MODE_NATURAL);
		}

		if (state.inputCursorVisible && layoutSnapshot.inputTextRect.right > layoutSnapshot.inputTextRect.left)
		{
			float cursorX = layoutSnapshot.inputTextRect.left + 1.0f;
			float cursorTop = layoutSnapshot.inputTextRect.top + 10.0f;
			float cursorBottom = layoutSnapshot.inputTextRect.bottom - 10.0f;
			if (inputTextLayout)
			{
				DWRITE_HIT_TEST_METRICS caretMetrics{};
				float caretPointX = 0.0f;
				float caretPointY = 0.0f;
				if (SUCCEEDED(inputTextLayout->HitTestTextPosition(
						static_cast<UINT32>(state.inputCaretIndex),
						FALSE,
						&caretPointX,
						&caretPointY,
						&caretMetrics)))
				{
					cursorX = inputTextOriginX + caretPointX;
					cursorTop = inputTextOriginY + caretPointY;
					cursorBottom = cursorTop + caretMetrics.height;
				}
			}

			renderTarget_->FillRectangle(
				D2D1::RectF(cursorX, cursorTop, cursorX + 1.5f, cursorBottom),
				textBrush_.Get());
		}

		renderTarget_->PopAxisAlignedClip();

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

		if (layoutSnapshot.hasOutputArea)
		{
			const auto drawTriangle = [&](const D2D1_RECT_F& rect, bool pointsUp)
			{
				Microsoft::WRL::ComPtr<ID2D1PathGeometry> geometry;
				if (FAILED(d2dFactory_->CreatePathGeometry(geometry.GetAddressOf())))
				{
					return;
				}

				Microsoft::WRL::ComPtr<ID2D1GeometrySink> sink;
				if (FAILED(geometry->Open(sink.GetAddressOf())))
				{
					return;
				}

				const auto centerX = (rect.left + rect.right) * 0.5f;
				const auto centerY = (rect.top + rect.bottom) * 0.5f;
				const auto left = D2D1::Point2F(rect.left, pointsUp ? rect.bottom : rect.top);
				const auto top = D2D1::Point2F(centerX, pointsUp ? rect.top : rect.bottom);
				const auto right = D2D1::Point2F(rect.right, pointsUp ? rect.bottom : rect.top);
				(void)centerY;

				sink->BeginFigure(left, D2D1_FIGURE_BEGIN_FILLED);
				sink->AddLine(top);
				sink->AddLine(right);
				sink->EndFigure(D2D1_FIGURE_END_CLOSED);
				if (SUCCEEDED(sink->Close()))
				{
					renderTarget_->FillGeometry(geometry.Get(), textBrush_.Get());
				}
			};

			if (state.outputCanPageUp)
			{
				drawTriangle(layoutSnapshot.outputScrollUpRect, true);
			}

			if (state.outputCanPageDown)
			{
				drawTriangle(layoutSnapshot.outputScrollDownRect, false);
			}
		}
	}

	if (state.visualMode == LuvLetterOverlayVisualMode_Badge && logoBitmap_)
	{
		renderTarget_->DrawBitmap(
			logoBitmap_.Get(),
			layoutSnapshot.logoRect,
			1.0f,
			D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
	}
	else if (state.visualMode == LuvLetterOverlayVisualMode_CommandLine)
	{
		const auto frameRect = InsetRect(layoutSnapshot.logoRect, 10.0f, 10.0f);
		const D2D1_ROUNDED_RECT roundedRect{ frameRect, 6.0f, 6.0f };
		renderTarget_->DrawRoundedRectangle(roundedRect, textBrush_.Get(), 1.4f);

		const auto width = frameRect.right - frameRect.left;
		const auto height = frameRect.bottom - frameRect.top;
		const auto chevronStart = D2D1::Point2F(frameRect.left + (width * 0.26f), frameRect.top + (height * 0.38f));
		const auto chevronMid = D2D1::Point2F(frameRect.left + (width * 0.40f), frameRect.top + (height * 0.50f));
		const auto chevronEnd = D2D1::Point2F(frameRect.left + (width * 0.26f), frameRect.top + (height * 0.62f));
		const auto underscoreLeft = D2D1::Point2F(frameRect.left + (width * 0.50f), frameRect.top + (height * 0.66f));
		const auto underscoreRight = D2D1::Point2F(frameRect.left + (width * 0.74f), frameRect.top + (height * 0.66f));

		renderTarget_->DrawLine(chevronStart, chevronMid, textBrush_.Get(), 1.6f);
		renderTarget_->DrawLine(chevronMid, chevronEnd, textBrush_.Get(), 1.6f);
		renderTarget_->DrawLine(underscoreLeft, underscoreRight, textBrush_.Get(), 1.6f);
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

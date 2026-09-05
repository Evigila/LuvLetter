#pragma once

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <string>
#include <utility>

namespace LuvLetterNative
{
	using MessageQueueClock = std::chrono::steady_clock;

	inline constexpr auto MessageLifetime = std::chrono::seconds(3);
	inline constexpr auto MessageShowDuration = std::chrono::milliseconds(180);
	inline constexpr auto MessageHideDuration = std::chrono::milliseconds(140);
	inline constexpr double MessageSpinnerPeriodMilliseconds = 800.0;

	struct MessageQueueEntry final
	{
		uint64_t token = 0;
		std::wstring text;
		MessageQueueClock::time_point createdAt;
		MessageQueueClock::time_point expiresAt;
		bool loading = false;

		[[nodiscard]] bool IsActiveActivity() const noexcept
		{
			return token != 0 && loading;
		}

		[[nodiscard]] bool HasFiniteLifetime() const noexcept
		{
			return expiresAt != (MessageQueueClock::time_point::max)();
		}

		[[nodiscard]] bool IsRemovalDue(MessageQueueClock::time_point now) const noexcept
		{
			return HasFiniteLifetime() && expiresAt + MessageHideDuration <= now;
		}

		void Update(std::wstring message)
		{
			text = std::move(message);
		}

		void Complete(
			std::wstring finalMessage,
			bool retainFinalMessage,
			MessageQueueClock::time_point now)
		{
			loading = false;
			if (retainFinalMessage)
			{
				text = std::move(finalMessage);
				expiresAt = now + MessageLifetime;
			}
			else
			{
				expiresAt = now;
			}
		}
	};

	[[nodiscard]] inline double CalculateMessageSpinnerRadians(
		MessageQueueClock::time_point createdAt,
		MessageQueueClock::time_point now) noexcept
	{
		constexpr double TwoPi = 6.283185307179586476925286766559;
		const auto elapsed = (std::max)(
			0.0,
			std::chrono::duration<double, std::milli>(now - createdAt).count());
		return std::fmod(elapsed, MessageSpinnerPeriodMilliseconds)
			/ MessageSpinnerPeriodMilliseconds * TwoPi;
	}
}

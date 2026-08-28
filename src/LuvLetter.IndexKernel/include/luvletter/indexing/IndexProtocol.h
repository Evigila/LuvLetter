#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace luvletter::indexing::protocol {

inline constexpr std::uint32_t kMagic = 0x58494C4C;
inline constexpr std::uint16_t kMajorVersion = 1;
inline constexpr std::uint32_t kHeaderSize = 20;
inline constexpr std::uint32_t kMaximumPayloadSize = 1U * 1024U * 1024U;

enum class MessageType : std::uint16_t {
    Hello = 1,
    HelloAck = 2,
    ConfigureRoots = 3,
    Query = 4,
    QueryResult = 5,
    Status = 6,
    Shutdown = 7,
    Error = 8,
};

struct FrameHeader final {
    std::uint32_t magic = kMagic;
    std::uint16_t majorVersion = kMajorVersion;
    MessageType type = MessageType::Error;
    std::uint32_t payloadLength = 0;
    std::uint64_t requestId = 0;
};

struct IndexStatus final {
    std::uint64_t generation = 0;
    bool rebuilding = false;
};

inline void AppendU16(std::vector<std::byte>& destination, const std::uint16_t value) {
    destination.push_back(static_cast<std::byte>(value & 0xFFU));
    destination.push_back(static_cast<std::byte>((value >> 8U) & 0xFFU));
}

inline void AppendU32(std::vector<std::byte>& destination, const std::uint32_t value) {
    for (unsigned shift = 0; shift < 32; shift += 8) {
        destination.push_back(static_cast<std::byte>((value >> shift) & 0xFFU));
    }
}

inline void AppendU64(std::vector<std::byte>& destination, const std::uint64_t value) {
    for (unsigned shift = 0; shift < 64; shift += 8) {
        destination.push_back(static_cast<std::byte>((value >> shift) & 0xFFU));
    }
}

inline bool ReadU16(const std::span<const std::byte> source, std::size_t& cursor, std::uint16_t& value) {
    if (source.size() - cursor < sizeof(value)) {
        return false;
    }

    value = static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(source[cursor])) |
        static_cast<std::uint16_t>(std::to_integer<std::uint8_t>(source[cursor + 1])) << 8U;
    cursor += sizeof(value);
    return true;
}

inline bool ReadU32(const std::span<const std::byte> source, std::size_t& cursor, std::uint32_t& value) {
    if (source.size() - cursor < sizeof(value)) {
        return false;
    }

    value = 0;
    for (unsigned shift = 0; shift < 32; shift += 8) {
        value |= static_cast<std::uint32_t>(std::to_integer<std::uint8_t>(source[cursor++])) << shift;
    }
    return true;
}

inline bool ReadU64(const std::span<const std::byte> source, std::size_t& cursor, std::uint64_t& value) {
    if (source.size() - cursor < sizeof(value)) {
        return false;
    }

    value = 0;
    for (unsigned shift = 0; shift < 64; shift += 8) {
        value |= static_cast<std::uint64_t>(std::to_integer<std::uint8_t>(source[cursor++])) << shift;
    }
    return true;
}

inline void AppendUtf8(std::vector<std::byte>& destination, const std::string_view value) {
    AppendU32(destination, static_cast<std::uint32_t>(value.size()));
    destination.insert(
        destination.end(),
        reinterpret_cast<const std::byte*>(value.data()),
        reinterpret_cast<const std::byte*>(value.data() + value.size()));
}

inline bool ReadUtf8(const std::span<const std::byte> source, std::size_t& cursor, std::string& value) {
    std::uint32_t length = 0;
    if (!ReadU32(source, cursor, length) || length > source.size() - cursor) {
        return false;
    }

    value.assign(reinterpret_cast<const char*>(source.data() + cursor), length);
    cursor += length;
    return true;
}

inline std::vector<std::byte> EncodeHeader(const FrameHeader& header) {
    std::vector<std::byte> bytes;
    bytes.reserve(kHeaderSize);
    AppendU32(bytes, header.magic);
    AppendU16(bytes, header.majorVersion);
    AppendU16(bytes, static_cast<std::uint16_t>(header.type));
    AppendU32(bytes, header.payloadLength);
    AppendU64(bytes, header.requestId);
    return bytes;
}

inline bool DecodeHeader(const std::span<const std::byte> bytes, FrameHeader& header) {
    if (bytes.size() != kHeaderSize) {
        return false;
    }

    std::size_t cursor = 0;
    std::uint16_t type = 0;
    return ReadU32(bytes, cursor, header.magic) &&
        ReadU16(bytes, cursor, header.majorVersion) &&
        ReadU16(bytes, cursor, type) &&
        ReadU32(bytes, cursor, header.payloadLength) &&
        ReadU64(bytes, cursor, header.requestId) &&
        (header.type = static_cast<MessageType>(type), true);
}

inline std::vector<std::byte> EncodeStatus(const IndexStatus status) {
    std::vector<std::byte> bytes;
    bytes.reserve(9);
    AppendU64(bytes, status.generation);
    bytes.push_back(status.rebuilding ? std::byte{1} : std::byte{0});
    return bytes;
}

inline bool DecodeStatus(const std::span<const std::byte> bytes, IndexStatus& status) {
    if (bytes.size() != 9) {
        return false;
    }
    std::size_t cursor = 0;
    if (!ReadU64(bytes, cursor, status.generation)) {
        return false;
    }
    const auto rebuilding = std::to_integer<std::uint8_t>(bytes[cursor]);
    if (rebuilding > 1) {
        return false;
    }
    status.rebuilding = rebuilding != 0;
    return true;
}

} // namespace luvletter::indexing::protocol

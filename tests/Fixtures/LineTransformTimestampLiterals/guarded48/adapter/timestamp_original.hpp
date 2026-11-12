// SPDX-License-Identifier: GPL-2.0-or-later
// 検証専用。Timestamp armのみ。原典型・関数・armはraw .incで無改変接続する。
#pragma once
#include <memory>
#include <string>
#include <vector>
#include <variant>
#include <optional>
#include <cstdint>
#include <stdexcept>
#include <type_traits>
#include <Poco/UnWindows.h>
#include <oleauto.h>
#include <Poco/Timestamp.h>
#include <Poco/DateTime.h>
#include <Poco/LocalDateTime.h>
#include <Poco/DateTimeParser.h>
#include <Poco/DateTimeFormatter.h>
#include <Poco/DateTimeFormat.h>
#include <Poco/Timezone.h>
#include <Poco/UnicodeConverter.h>
#include <Poco/Exception.h>
#include "UnicodeString.h"
#include "FilterError.h"
#include "ValueType.inc"
#include "RealFilterEvalContext.inc"
#include "ExprNode.inc"
#include "DateTimeLiteral.inc"
namespace ucr {
using Poco::UnicodeConverter;
#include "Ucs4_to_Utf8.inc"
#include "ucr-toUTF8-wide.inc"
#include "ucr-toUTF8-return.inc"
#include "ucr-toTString.inc"
}
namespace locality {
using Poco::Timestamp;
#include "WindowsLocaleFallback.inc"
}
#include "ParseDateTime.inc"
#include "DateTimeCtor.inc"
static std::string SerializeTimestampArmOnly(const ValueType& val)
{
#include "TimestampSerializerArm.inc"
    throw std::invalid_argument("adapter protocol permits only original Poco::Timestamp arm");
}
static_assert(std::is_same_v<std::variant_alternative_t<4, ValueType>, Poco::Timestamp>);
static_assert(sizeof(Poco::Timestamp::TimeVal)==8 && std::is_signed_v<Poco::Timestamp::TimeVal>);

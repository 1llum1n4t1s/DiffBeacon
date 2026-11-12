// SPDX-License-Identifier: GPL-2.0-or-later
// Timestamp constructor/Evaluate/原Timestamp arm限定のNative採取準備。実行未承認。
#include "timestamp_original.hpp"
#include <iostream>
#include <sstream>
#include <typeinfo>
#include <utility>
struct CaptureCase { const char* id; const char* inputHex; };
static constexpr CaptureCase cases[] = {
    {"epoch-explicit-z", "313937302d30312d30315430303a30303a30305a"},
    {"epoch-lower-z", "313937302d30312d30315430303a30303a30307a"},
    {"epoch-plus-zone", "313937302d30312d30315430303a30303a30302b30303a3030"},
    {"epoch-negative-only-zone", "313937302d30312d30315430303a30303a30302d30303a3030"},
    {"positive-offset", "323030302d30312d30325430333a30343a30352b30393a3030"},
    {"negative-offset-quirk", "323030302d30312d30325430333a30343a30352d30393a3030"},
    {"local-no-zone", "323030302d30312d30325430333a30343a3035"},
    {"date-only", "323030302d30312d3032"},
    {"before-epoch", "313936392d31322d33315432333a35393a35395a"},
    {"fraction-1", "313937302d30312d30315430303a30303a30302e315a"},
    {"fraction-3", "313937302d30312d30315430303a30303a30302e3132335a"},
    {"fraction-6", "313937302d30312d30315430303a30303a30302e3132333435365a"},
    {"fraction-more-6", "313937302d30312d30315430303a30303a30302e3132333435363738395a"},
    {"leap-date", "323030302d30322d32395431323a33343a35365a"},
    {"invalid-leap-date", "313930302d30322d32395431323a33343a35365a"},
    {"year-zero-candidate", "303030302d30312d30315430303a30303a30305a"},
    {"year-above-managed-candidate", "31303030302d30312d30315430303a30303a30305a"},
    {"invalid-month", "323030302d31332d30315430303a30303a30305a"},
    {"invalid-time", "323030302d30312d30315432353a36313a36315a"},
    {"empty-literal", ""},
    {"invalid-text", "6e6f7420612064617465"},
    {"slash-format", "323030302f30312f30322030333a30343a3035"},
    {"dot-format", "323030302e30312e30322030333a30343a3035"},
    {"named-month", "30322d4a616e2d323030302030333a30343a3035"},
    {"locale-fallback-candidate", "32303030e5b9b431e69c8832e697a52033e6998234e5888635e7a792"},
    {"trailing-text", "313937302d30312d30315430303a30303a30305a20747261696c696e67"},
    {"dst-summer-candidate", "323032362d30372d30315431323a30303a3030"},
    {"dst-winter-candidate", "323032362d30312d30315431323a30303a3030"},
    {"dst-gap-candidate", "323032362d30332d30385430323a33303a3030"},
    {"dst-fold-candidate", "323032362d31312d30315430313a33303a3030"}
};
static_assert(sizeof(cases)/sizeof(cases[0])==31);
static std::string Hex(const std::string& bytes) {
    static constexpr char alphabet[]="0123456789abcdef";
    std::string result; result.reserve(bytes.size()*2);
    for (unsigned char b : bytes) { result.push_back(alphabet[b>>4]); result.push_back(alphabet[b&15]); }
    return result;
}
static std::string DecodeHex(const char* text) {
    std::string hex(text); if ((hex.size()&1)!=0 || hex.size()>512) throw std::invalid_argument("invalid case hex");
    auto digit=[](char c)->unsigned { if(c>='0'&&c<='9')return c-'0'; if(c>='a'&&c<='f')return c-'a'+10; throw std::invalid_argument("noncanonical case hex"); };
    std::string result; result.reserve(hex.size()/2);
    for(std::size_t i=0;i<hex.size();i+=2)result.push_back(static_cast<char>((digit(hex[i])<<4)|digit(hex[i+1])));
    return result;
}
static std::string Json(const std::string& bytes) {
    std::string result="\"";
    for(unsigned char c:bytes) { if(c=='\"'||c=='\\') {result.push_back('\\');result.push_back(static_cast<char>(c));} else if(c<32)throw std::invalid_argument("envelope requires escaped/raw-hex byte fields"); else result.push_back(static_cast<char>(c)); }
    result.push_back('\"'); return result;
}
static std::uint64_t FileTimeValue(const FILETIME& time) { return (static_cast<std::uint64_t>(time.dwHighDateTime)<<32)|time.dwLowDateTime; }
static std::uint64_t CurrentProcessBirth() {
    FILETIME born{},exit{},kernel{},user{}; if(!GetProcessTimes(GetCurrentProcess(),&born,&exit,&kernel,&user))throw std::runtime_error("GetProcessTimes failed"); return FileTimeValue(born);
}
static std::string ClockMicroseconds() { return std::to_string(Poco::Timestamp().epochMicroseconds()); }
int main(int argc,char**) {
    if(argc!=1) {std::cerr<<"only fixed31 original constructor cases are supported\n";return 64;}
    try {
        const DWORD pid=GetCurrentProcessId(); const auto birth=CurrentProcessBirth();
        wchar_t localeName[LOCALE_NAME_MAX_LENGTH]{};
        const int localeLength=GetUserDefaultLocaleName(localeName,LOCALE_NAME_MAX_LENGTH);
        const std::string localeUtf8=localeLength>0?ucr::toUTF8(String(localeName)):std::string();
        TIME_ZONE_INFORMATION zone{};const DWORD zoneState=GetTimeZoneInformation(&zone);
        if(zoneState==TIME_ZONE_ID_INVALID)throw std::runtime_error("GetTimeZoneInformation failed");
        const std::string zoneName=ucr::toUTF8(String(zone.StandardName));
        for(const auto& item:cases) {
            const std::string input=DecodeHex(item.inputHex);
            const std::string before=ClockMicroseconds(); const int tzd=Poco::Timezone::tzd();
            FilterEvalContext context{};
            if(context.expr||context.di||context.provider||context.sharedContext||context.lineIndex!=-1)throw std::logic_error("original context defaults changed");
            std::string phase="constructor",status="error",typeNameHex,errorHex,serializedHex,epoch;
            std::size_t valueIndex=0;
            try {
                DateTimeLiteral literal(input); phase="evaluate";
                const ValueType value=literal.Evaluate(context); valueIndex=value.index();
                const auto* timestamp=std::get_if<Poco::Timestamp>(&value);
                if(!timestamp||valueIndex!=4)throw std::logic_error("original DateTimeLiteral returned non-Timestamp");
                epoch=std::to_string(timestamp->epochMicroseconds()); phase="timestamp-arm-serialization";
                serializedHex=Hex(SerializeTimestampArmOnly(value)); status="ok"; phase.clear();
            } catch(const std::exception& e) {typeNameHex=Hex(typeid(e).name());errorHex=Hex(e.what());}
            const std::string after=ClockMicroseconds();
            std::ostringstream row;
            row<<"{\"id\":"<<Json(item.id)<<",\"scope\":\"original-ctor-evaluate-timestamp-arm-only\",\"inputUtf8Hex\":"<<Json(Hex(input))
               <<",\"sourceCommit\":\"6780924ba35f7f55af0f6f14dd302561175830a6\",\"originalNodesSha256\":\"a2fcca950bd5e5cfa48f3ff43e4436ae0f5fd55eff9661da1d5a979cc9051135\""
               <<",\"status\":"<<Json(status)<<",\"errorPhase\":"<<Json(phase)<<",\"exceptionTypeNameHex\":"<<Json(typeNameHex)<<",\"errorMessageHex\":"<<Json(errorHex)
               <<",\"valueTypeIndex\":"<<valueIndex<<",\"rawType\":"<<Json(epoch.empty()?std::string():std::string("Poco::Timestamp"))<<",\"epochMicrosecondsInt64Decimal\":"<<Json(epoch)<<",\"timestampArmSerializedUtf8Hex\":"<<Json(serializedHex)
               <<",\"contextLineIndex\":"<<context.lineIndex<<",\"contextPointersNull\":true,\"processId\":"<<pid<<",\"processBirthFileTimeDecimal\":"<<Json(std::to_string(birth))
               <<",\"userLcid\":"<<GetUserDefaultLCID()<<",\"userLocaleUtf8Hex\":"<<Json(Hex(localeUtf8))<<",\"timezoneState\":"<<zoneState<<",\"timezoneStandardNameUtf8Hex\":"<<Json(Hex(zoneName))
               <<",\"timezoneBiasMinutes\":"<<zone.Bias<<",\"timezoneStandardBiasMinutes\":"<<zone.StandardBias<<",\"timezoneDaylightBiasMinutes\":"<<zone.DaylightBias<<",\"pocoTimezoneTzdSeconds\":"<<tzd
               <<",\"clockBeforeEpochMicrosecondsInt64Decimal\":"<<Json(before)<<",\"clockAfterEpochMicrosecondsInt64Decimal\":"<<Json(after)<<"}";
            std::cout<<row.str()<<'\n'; if(!std::cout)return 74;
        }
        std::cout.flush();return std::cout?0:74;
    } catch(const std::exception& e) {std::cerr<<"capturer envelope failure: "<<e.what()<<'\n';return 70;}
}

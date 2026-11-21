// SPDX-License-Identifier: GPL-2.0-or-later
// 日時境界の検証専用adapter。guardは原典外。原典の型・Evaluate・armは変更しない。
#include "timestamp_original.hpp"
#include <iostream>
#include <sstream>
#include <typeinfo>
#include <utility>
#include <limits>
struct CaptureCase { const char* id; const char* callerKind; const char* inputHex; const char* rawDecimal; };
static constexpr CaptureCase cases[] = {
    {"raw-int64-min","rawTimestampCtor","","-9223372036854775808"},
    {"raw-int64-max","rawTimestampCtor","","9223372036854775807"},
    {"raw-epoch-minus-second","rawTimestampCtor","","-1000000"},
    {"raw-epoch-minus-us","rawTimestampCtor","","-1"},
    {"raw-epoch-zero","rawTimestampCtor","","0"},
    {"raw-epoch-plus-us","rawTimestampCtor","","1"},
    {"raw-epoch-plus-second","rawTimestampCtor","","1000000"},
    {"raw-2038-last-second","rawTimestampCtor","","2147483647000000"},
    {"raw-2038-next-second","rawTimestampCtor","","2147483648000000"},
    {"raw-3000-end","rawTimestampCtor","","32535215999000000"},
    {"raw-3001-start","rawTimestampCtor","","32535216000000000"},
    {"raw-9999-end","rawTimestampCtor","","253402300799000000"},
    {"string-year-0000","stringCtor","303030302d30312d30315430303a30303a30305a",""},
    {"string-year-0001","stringCtor","303030312d30312d30315430303a30303a30305a",""},
    {"string-year-0004-leap","stringCtor","303030342d30322d32395430303a30303a30305a",""},
    {"string-year-0100-not-leap","stringCtor","303130302d30322d32395430303a30303a30305a",""},
    {"string-year-0400-leap","stringCtor","303430302d30322d32395430303a30303a30305a",""},
    {"string-year-1900-not-leap","stringCtor","313930302d30322d32395430303a30303a30305a",""},
    {"string-year-2000-leap","stringCtor","323030302d30322d32395430303a30303a30305a",""},
    {"string-2038-last-second","stringCtor","323033382d30312d31395430333a31343a30375a",""},
    {"string-2038-next-second","stringCtor","323033382d30312d31395430333a31343a30385a",""},
    {"string-3000-end","stringCtor","333030302d31322d33315432333a35393a35395a",""},
    {"string-3001-start","stringCtor","333030312d30312d30315430303a30303a30305a",""},
    {"string-9999-end","stringCtor","393939392d31322d33315432333a35393a35395a",""},
    {"string-10000-start","stringCtor","31303030302d30312d30315430303a30303a30305a",""},
    {"string-fraction-none","stringCtor","313937302d30312d30315430303a30303a30305a",""},
    {"string-fraction-empty","stringCtor","313937302d30312d30315430303a30303a30302e5a",""},
    {"string-fraction-zero","stringCtor","313937302d30312d30315430303a30303a30302e305a",""},
    {"string-fraction-6-zero","stringCtor","313937302d30312d30315430303a30303a30302e3030303030305a",""},
    {"string-fraction-6-one","stringCtor","313937302d30312d30315430303a30303a30302e3030303030315a",""},
    {"string-fraction-6-max","stringCtor","313937302d30312d30315430303a30303a30302e3939393939395a",""},
    {"string-fraction-7-one","stringCtor","313937302d30312d30315430303a30303a30302e313030303030305a",""},
    {"string-fraction-7","stringCtor","313937302d30312d30315430303a30303a30302e313233343536375a",""},
    {"string-fraction-9","stringCtor","313937302d30312d30315430303a30303a30302e3132333435363738395a",""},
    {"string-date-dash","stringCtor","323030302d30312d3032",""},
    {"string-date-slash","stringCtor","323030302f30312f3032",""},
    {"string-date-dot","stringCtor","323030302e30312e3032",""},
    {"string-format-slash","stringCtor","323030302f30312f30322030333a30343a3035",""},
    {"string-format-dot","stringCtor","323030302e30312e30322030333a30343a3035",""},
    {"string-format-named-month","stringCtor","30322d4a616e2d323030302030333a30343a3035",""},
    {"string-trailing-iso","stringCtor","313937302d30312d30315430303a30303a30305a20747261696c696e67",""},
    {"string-trailing-date","stringCtor","323030302d30312d303220747261696c696e67",""},
    {"string-trailing-slash","stringCtor","323030302f30312f30322030333a30343a303520747261696c696e67",""},
    {"string-trailing-named-month","stringCtor","30322d4a616e2d323030302030333a30343a303520747261696c696e67",""},
    {"string-locale-1900","stringCtor","31393030e5b9b431e69c8832e697a52033e6998234e5888635e7a792",""},
    {"string-locale-1969","stringCtor","31393639e5b9b43132e69c883331e697a5203233e699823539e588863539e7a792",""},
    {"string-locale-1970","stringCtor","31393730e5b9b431e69c8831e697a52030e6998230e5888630e7a792",""},
    {"string-embedded-nul-forensic-guard","stringCtor","313937302d30312d3031005430303a30303a30305a",""}
};
static_assert(sizeof(cases)/sizeof(cases[0])==48);
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
    if(argc!=1) {std::cerr<<"only fixed48 boundary cases are supported\n";return 64;}
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
            bool serializationRequested=false; std::string serializationStatus="not-reached";
            try {
                const DateTimeLiteral literal=std::string(item.callerKind)=="rawTimestampCtor"
                    ? DateTimeLiteral(Poco::Timestamp(static_cast<Poco::Timestamp::TimeVal>(std::stoll(item.rawDecimal))))
                    : DateTimeLiteral(input); phase="evaluate";
                const ValueType value=literal.Evaluate(context); valueIndex=value.index();
                const auto* timestamp=std::get_if<Poco::Timestamp>(&value);
                if(!timestamp||valueIndex!=4)throw std::logic_error("original DateTimeLiteral returned non-Timestamp");
                epoch=std::to_string(timestamp->epochMicroseconds());
                // adapter-only: original Timestamp.h:230-233 signed multiply/add must be defined.
                using Int64=Poco::Timestamp::TimeVal;
                constexpr Int64 utcOffset=(Int64(0x01b21dd2)<<32)+0x13814000;
                static_assert(utcOffset==122192928000000000LL);
                const Int64 rawEpoch=timestamp->epochMicroseconds();
                serializationRequested=rawEpoch>=std::numeric_limits<Int64>::min()/10
                    && rawEpoch<=(std::numeric_limits<Int64>::max()-utcOffset)/10;
                if(serializationRequested) {
                    serializationStatus="requested"; phase="timestamp-arm-serialization";
                    serializedHex=Hex(SerializeTimestampArmOnly(value)); serializationStatus="completed";
                } else {serializationStatus="not-requested-unsafe-original-utcTime";}
                status="ok"; phase.clear();
            } catch(const std::exception& e) {typeNameHex=Hex(typeid(e).name());errorHex=Hex(e.what());}
            const std::string after=ClockMicroseconds();
            std::ostringstream row;
            row<<"{\"id\":"<<Json(item.id)<<",\"scope\":\"original-ctor-evaluate-timestamp-arm-only-with-adapter-utcTime-guard\",\"inputUtf8Hex\":"<<Json(Hex(input))
               <<",\"callerKind\":"<<Json(item.callerKind)<<",\"inputRawTimestampInt64Decimal\":"<<(std::string(item.callerKind)=="rawTimestampCtor"?Json(item.rawDecimal):std::string("null"))
               <<",\"serializationRequested\":"<<(serializationRequested?"true":"false")<<",\"serializationStatus\":"<<Json(serializationStatus)
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

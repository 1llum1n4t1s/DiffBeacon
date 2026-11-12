namespace Poco { class RegularExpression; }
struct FilterExpression;
struct FileContentRef;
class DIFFITEM;
struct ValueType2;
using ValueType = std::variant<std::monostate, bool, double, int64_t, Poco::Timestamp, std::shared_ptr<Poco::RegularExpression>, std::string, std::shared_ptr<FileContentRef>, std::shared_ptr<std::vector<ValueType2>>>;
struct ValueType2 { ValueType value; };

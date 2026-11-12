std::string ToStringValue(const ValueType& val)
{
	if (auto intVal = std::get_if<int64_t>(&val))
		return std::to_string(*intVal);
	if (auto doubleVal = std::get_if<double>(&val))
		return std::to_string(*doubleVal);
	if (auto strVal = std::get_if<std::string>(&val))
		return *strVal;
	if (auto boolVal = std::get_if<bool>(&val))
		return *boolVal ? "true" : "false";
	if (auto tsVal = std::get_if<Poco::Timestamp>(&val))
	{
		try
		{
			Poco::LocalDateTime ldt(*tsVal);
			return Poco::DateTimeFormatter::format(ldt, "%Y-%m-%d %H:%M:%S");
		}
		catch (Poco::Exception& e)
		{
			return e.what();
		}
	}
	if (auto arrayVal = std::get_if<std::shared_ptr<std::vector<ValueType2>>>(&val))
	{
		std::string result = "[";
		bool first = true;
		for (const auto& item : **arrayVal)
		{
			if (!first) result += ", ";
			result += ToStringValue(item.value);
			first = false;
		}
		result += "]";
		return result;
	}
	if (auto contentVal = std::get_if<std::shared_ptr<FileContentRef>>(&val))
		return std::string("<FileContent:") + ucr::toUTF8((*contentVal)->path) + ">";
	if (auto regexVal = std::get_if<std::shared_ptr<Poco::RegularExpression>>(&val))
		return "<Regex>";
	if (std::holds_alternative<std::monostate>(val))
		return "none";
	return "<unknown>";
}
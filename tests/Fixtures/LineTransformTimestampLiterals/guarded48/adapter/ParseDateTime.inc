static std::optional<Poco::Timestamp> ParseDateTime(const std::string& str)
{
	static const std::vector<std::string> formats = {
		Poco::DateTimeFormat::ISO8601_FORMAT,
		Poco::DateTimeFormat::ISO8601_FRAC_FORMAT,
		"%Y/%m/%d %H:%M:%S",
		"%Y.%m.%d %H:%M:%S",
		"%d-%b-%Y %H:%M:%S",
		"%Y-%m-%d",
		"%Y/%m/%d",
		"%Y.%m.%d",
	};

	Poco::DateTime dt;
	for (const auto& fmt : formats)
	{
		try
		{
			int tz = 0;
			Poco::DateTimeParser::parse(fmt, str, dt, tz);
			dt.makeUTC((str.find_first_of("zZ+") != std::string::npos) ? tz : Poco::Timezone::tzd());
			return dt.timestamp();
		}
		catch (Poco::SyntaxException&)
		{
			// Try next format
		}
	}

	// Try platform-specific parsing using Windows locale-aware VarDateFromStr
	int64_t result;
	if (locality::ParseDateTime(ucr::toTString(str), result))
		return Poco::Timestamp(result);

	return std::nullopt;
}

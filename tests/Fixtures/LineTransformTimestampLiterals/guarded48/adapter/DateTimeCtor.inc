DateTimeLiteral::DateTimeLiteral(const std::string& v)
{
	auto timestamp = ParseDateTime(v);
	if (!timestamp)
		throw InvalidArgumentError("Unrecognized date/time format: " + v);
	value = *timestamp;
}

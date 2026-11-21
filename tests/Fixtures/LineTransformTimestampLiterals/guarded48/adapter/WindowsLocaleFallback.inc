bool ParseDateTime(const String& str, int64_t& result)
{
	if (str.empty())
		return false;

	DATE oleDate = 0.0;
	HRESULT hr = VarDateFromStr(str.c_str(), LOCALE_USER_DEFAULT, 0, &oleDate);
	if (FAILED(hr))
		return false;

	// Convert OLE DATE to SYSTEMTIME
	SYSTEMTIME st{};
	if (!VariantTimeToSystemTime(oleDate, &st))
		return false;

	// Convert SYSTEMTIME to FILETIME (local time)
	FILETIME ftLocal{};
	if (!SystemTimeToFileTime(&st, &ftLocal))
		return false;

	// Convert local FILETIME to UTC FILETIME
	FILETIME ftUtc{};
	if (!LocalFileTimeToFileTime(&ftLocal, &ftUtc))
		return false;

	// Convert FILETIME to Poco::Timestamp
	unsigned int low = ftUtc.dwLowDateTime;
	unsigned int high = ftUtc.dwHighDateTime;
	Timestamp ts = Timestamp::fromFileTimeNP(low, high);

	// Convert to microseconds since epoch
	result = ts.epochMicroseconds();
	return true;
}

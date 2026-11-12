inline Timestamp::UtcTimeVal Timestamp::utcTime() const
{
	return _ts*10 + (TimeDiff(0x01b21dd2) << 32) + 0x13814000;
}

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

if (functionName == "strfind" && args && args->size() >= 2 &&
		dynamic_cast<StringLiteral*>((*args)[0]) && dynamic_cast<StringLiteral*>((*args)[1]) &&
		(args->size() == 2 || dynamic_cast<IntLiteral*>((*args)[2])))
	{
		ValueType result = func(ectxt, args);
		if (auto intResult = std::get_if<int64_t>(&result))
			return ReplaceFunctionWithLiteral(this, *intResult);
	}
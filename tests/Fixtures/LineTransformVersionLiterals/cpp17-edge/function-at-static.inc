if (functionName == "at" && args && args->size() >= 2)
	{
		if (auto arrayLit = dynamic_cast<ArrayLiteral*>((*args)[0]))
		{
			if (auto intLit = dynamic_cast<IntLiteral*>((*args)[1]))
			{
				int64_t index = intLit->value;
				if (index < 0)
					index += static_cast<int64_t>(arrayLit->value->size());
				if (index >= 0 && index < static_cast<int64_t>(arrayLit->value->size()))
					return CreateLiteralFromArrayElement(this, (*arrayLit->value)[static_cast<size_t>(index)].value);
			}
		}
	}
struct DateTimeLiteral : public ExprNode
{
	DateTimeLiteral(const std::string& v);
	DateTimeLiteral(const Poco::Timestamp& v) : value(v) { }
	inline ValueType Evaluate(const FilterEvalContext& ectxt) const override { return value; }
	Poco::Timestamp value;
};

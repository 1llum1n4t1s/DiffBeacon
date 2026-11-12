struct VersionLiteral : public ExprNode
{
	VersionLiteral(const std::string& v);
	inline ValueType Evaluate(const FilterEvalContext& ectxt) const override { return value; }
	int64_t value;
};
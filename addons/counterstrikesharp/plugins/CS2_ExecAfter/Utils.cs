namespace CS2_ExecAfter
{
	public partial class CS2_ExecAfter
	{

		public static string StripQuotes(string input)
		{
			// Check if the input is not null or empty
			if (!string.IsNullOrEmpty(input))
			{
				// Check if the string starts and ends with quotes
				if (input.StartsWith("\"") && input.EndsWith("\""))
				{
					// Remove the first and last characters
					return input.Substring(1, input.Length - 2);
				}
			}

			// Return the original string if no quotes are found
			return input;
		}

	}
}
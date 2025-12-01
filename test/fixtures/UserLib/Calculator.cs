using System;
using System.Collections.Generic;

namespace MyCompany.Utils
{
    public class Calculator
    {
        public int Add(int a, int b) => a + b;
        public int Subtract(int a, int b) => a - b;
        public List<int> GetRange(int start, int count)
        {
            var result = new List<int>();
            for (int i = 0; i < count; i++)
            {
                result.Add(start + i);
            }
            return result;
        }
    }

    public class StringHelper
    {
        public string Reverse(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            char[] chars = input.ToCharArray();
            Array.Reverse(chars);
            return new string(chars);
        }
    }
}

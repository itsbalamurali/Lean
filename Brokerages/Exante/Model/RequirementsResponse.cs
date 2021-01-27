

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using System.ComponentModel.DataAnnotations;
using OpenAPIDateConverter = OpenAPIDateConverter;

namespace QuantConnect.Brokerages.Exante.Model
{
    /// <summary>
    /// RequirementsResponse
    /// </summary>
    [DataContract(Name = "RequirementsResponse")]
    public partial class RequirementsResponse : IEquatable<RequirementsResponse>, IValidatableObject
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RequirementsResponse" /> class.
        /// </summary>
        [JsonConstructorAttribute]
        protected RequirementsResponse() { }
        /// <summary>
        /// Initializes a new instance of the <see cref="RequirementsResponse" /> class.
        /// </summary>
        /// <param name="priceUnit">instrument price unit (required).</param>
        /// <param name="units">instrument units name.</param>
        /// <param name="lotSize">instrument lot size value (required).</param>
        /// <param name="leverage">instrument leverage rate value (required).</param>
        /// <param name="contractMultiplier">instrument contract multiplier (required).</param>
        public RequirementsResponse(string priceUnit = default(string), string units = default(string), string lotSize = default(string), string leverage = default(string), string contractMultiplier = default(string))
        {
            // to ensure "priceUnit" is required (not null)
            this.PriceUnit = priceUnit ?? throw new ArgumentNullException("priceUnit is a required property for RequirementsResponse and cannot be null");
            // to ensure "lotSize" is required (not null)
            this.LotSize = lotSize ?? throw new ArgumentNullException("lotSize is a required property for RequirementsResponse and cannot be null");
            // to ensure "leverage" is required (not null)
            this.Leverage = leverage ?? throw new ArgumentNullException("leverage is a required property for RequirementsResponse and cannot be null");
            // to ensure "contractMultiplier" is required (not null)
            this.ContractMultiplier = contractMultiplier ?? throw new ArgumentNullException("contractMultiplier is a required property for RequirementsResponse and cannot be null");
            this.Units = units;
        }

        /// <summary>
        /// instrument price unit
        /// </summary>
        /// <value>instrument price unit</value>
        [DataMember(Name = "priceUnit", IsRequired = true, EmitDefaultValue = false)]
        public string PriceUnit { get; set; }

        /// <summary>
        /// instrument units name
        /// </summary>
        /// <value>instrument units name</value>
        [DataMember(Name = "units", EmitDefaultValue = false)]
        public string Units { get; set; }

        /// <summary>
        /// instrument lot size value
        /// </summary>
        /// <value>instrument lot size value</value>
        [DataMember(Name = "lotSize", IsRequired = true, EmitDefaultValue = false)]
        public string LotSize { get; set; }

        /// <summary>
        /// instrument leverage rate value
        /// </summary>
        /// <value>instrument leverage rate value</value>
        [DataMember(Name = "leverage", IsRequired = true, EmitDefaultValue = false)]
        public string Leverage { get; set; }

        /// <summary>
        /// instrument contract multiplier
        /// </summary>
        /// <value>instrument contract multiplier</value>
        [DataMember(Name = "contractMultiplier", IsRequired = true, EmitDefaultValue = false)]
        public string ContractMultiplier { get; set; }

        /// <summary>
        /// Returns the string presentation of the object
        /// </summary>
        /// <returns>String presentation of the object</returns>
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("class RequirementsResponse {\n");
            sb.Append("  PriceUnit: ").Append(PriceUnit).Append("\n");
            sb.Append("  Units: ").Append(Units).Append("\n");
            sb.Append("  LotSize: ").Append(LotSize).Append("\n");
            sb.Append("  Leverage: ").Append(Leverage).Append("\n");
            sb.Append("  ContractMultiplier: ").Append(ContractMultiplier).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Returns the JSON string presentation of the object
        /// </summary>
        /// <returns>JSON string presentation of the object</returns>
        public virtual string ToJson()
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// Returns true if objects are equal
        /// </summary>
        /// <param name="input">Object to be compared</param>
        /// <returns>Boolean</returns>
        public override bool Equals(object input)
        {
            return this.Equals(input as RequirementsResponse);
        }

        /// <summary>
        /// Returns true if RequirementsResponse instances are equal
        /// </summary>
        /// <param name="input">Instance of RequirementsResponse to be compared</param>
        /// <returns>Boolean</returns>
        public bool Equals(RequirementsResponse input)
        {
            if (input == null)
                return false;

            return 
                (
                    this.PriceUnit == input.PriceUnit ||
                    (this.PriceUnit != null &&
                    this.PriceUnit.Equals(input.PriceUnit))
                ) && 
                (
                    this.Units == input.Units ||
                    (this.Units != null &&
                    this.Units.Equals(input.Units))
                ) && 
                (
                    this.LotSize == input.LotSize ||
                    (this.LotSize != null &&
                    this.LotSize.Equals(input.LotSize))
                ) && 
                (
                    this.Leverage == input.Leverage ||
                    (this.Leverage != null &&
                    this.Leverage.Equals(input.Leverage))
                ) && 
                (
                    this.ContractMultiplier == input.ContractMultiplier ||
                    (this.ContractMultiplier != null &&
                    this.ContractMultiplier.Equals(input.ContractMultiplier))
                );
        }

        /// <summary>
        /// Gets the hash code
        /// </summary>
        /// <returns>Hash code</returns>
        public override int GetHashCode()
        {
            unchecked // Overflow is fine, just wrap
            {
                int hashCode = 41;
                if (this.PriceUnit != null)
                    hashCode = hashCode * 59 + this.PriceUnit.GetHashCode();
                if (this.Units != null)
                    hashCode = hashCode * 59 + this.Units.GetHashCode();
                if (this.LotSize != null)
                    hashCode = hashCode * 59 + this.LotSize.GetHashCode();
                if (this.Leverage != null)
                    hashCode = hashCode * 59 + this.Leverage.GetHashCode();
                if (this.ContractMultiplier != null)
                    hashCode = hashCode * 59 + this.ContractMultiplier.GetHashCode();
                return hashCode;
            }
        }

        /// <summary>
        /// To validate all properties of the instance
        /// </summary>
        /// <param name="validationContext">Validation context</param>
        /// <returns>Validation Result</returns>
        IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext)
        {
            yield break;
        }
    }

}
